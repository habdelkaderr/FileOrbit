using FileOrbit.Core.Capabilities;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileOrbit.Engines.Media;

/// <summary>
/// Audio and video conversion with Windows Media Foundation (Windows.Media.Transcoding). Built into
/// Windows; uses hardware encoders when available. Supports the codecs Windows ships with
/// (MP3, AAC, WMA, FLAC, PCM, H.264). FFmpeg is preferred automatically when installed.
/// </summary>
public sealed class MediaFoundationEngine : ConversionEngineBase
{
    public const string EngineId = "media-foundation";

    private static readonly string[] AudioSources = ["mp3", "wav", "m4a", "aac", "wma", "flac"];
    private static readonly string[] VideoSources = ["mp4", "mov", "wmv", "avi", "mkv"];
    private static readonly string[] AudioTargets = ["mp3", "m4a", "wav", "wma", "flac"];
    private static readonly string[] VideoTargets = ["mp4", "wmv"];

    public override string Id => EngineId;

    public override string DisplayName => "Windows Media Foundation";

    public override string Description => "Built-in audio and video conversion using the codecs that ship with Windows.";

    protected override EngineStatus CheckStatus() => EngineStatus.Available("Ready (built into Windows)");

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var source in AudioSources.Concat(VideoSources))
        {
            var isVideo = VideoSources.Contains(source);
            foreach (var target in AudioTargets)
            {
                if (target == source)
                {
                    continue;
                }

                yield return Cap(source, target, target is "wav" or "flac" ? Fidelity.Lossless : Fidelity.Lossy, quality: 60) with
                {
                    Note = isVideo ? "Extracts the audio track" : target is "wav" or "flac" ? "Lossless container; quality cannot exceed the source" : null,
                    Options = AudioOptions(target),
                    Cost = isVideo ? ResourceCost.Medium : ResourceCost.Low,
                };
            }

            if (!isVideo)
            {
                continue;
            }

            foreach (var target in VideoTargets.Where(t => t != source))
            {
                yield return Cap(source, target, Fidelity.Lossy, quality: 60) with
                {
                    Note = target == "mp4" ? "H.264 video with AAC audio" : "Windows Media Video",
                    Options = [ResolutionOption, VideoQualityOption],
                    Cost = ResourceCost.High,
                };
            }
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        context.Report(null, "Analysing media");
        using var source = await OpenSourceAsync(context.Input.Path, cancellationToken);

        MediaEncodingProfile? sourceProfile = null;
        try
        {
            sourceProfile = await MediaEncodingProfile.CreateFromStreamAsync(source).AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Log.Write(Core.Logging.LogLevel.Warning, "Could not read source media properties", new Dictionary<string, object?> { ["error"] = ex.Message });
        }

        source.Seek(0);
        var profile = BuildProfile(context, sourceProfile);

        // Stream-based output: WinRT StorageFolder refuses FileOrbit's hidden staging folder.
        await using var outputFile = new FileStream(context.PrimaryOutputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true);
        using var destination = outputFile.AsRandomAccessStream();

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        PrepareTranscodeResult prepared;
        try
        {
            prepared = await transcoder.PrepareStreamTranscodeAsync(source, destination, profile).AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConversionException("Windows could not prepare this conversion. The file may be damaged or use an unsupported codec.", ex.Message, ex);
        }

        if (!prepared.CanTranscode)
        {
            throw new ConversionException(prepared.FailureReason switch
            {
                TranscodeFailureReason.CodecNotFound => "Windows does not have a codec for this file's audio or video. Installing FFmpeg adds support for many more codecs.",
                TranscodeFailureReason.InvalidProfile => "Windows cannot encode with these settings. Try a different quality or resolution.",
                _ => "Windows cannot convert this file. Installing FFmpeg adds support for many more formats.",
            }, prepared.FailureReason.ToString());
        }

        context.Report(0, "Converting");
        var progress = new Progress<double>(percent => context.Report(percent / 100.0, "Converting"));
        try
        {
            await prepared.TranscodeAsync().AsTask(cancellationToken, progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConversionException("The conversion failed while encoding.", ex.Message, ex);
        }

        await destination.FlushAsync().AsTask(cancellationToken);
        context.Report(1, "Done");
    }

    /// <summary>
    /// Opens the source through StorageFile when possible (it carries a MIME type that helps Media
    /// Foundation pick a decoder), falling back to a plain file stream.
    /// </summary>
    private static async Task<IRandomAccessStream> OpenSourceAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
            return await file.OpenReadAsync().AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true).AsRandomAccessStream();
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                throw new ConversionException("Windows could not open the media file.", inner.Message, inner);
            }
        }
    }

    private static MediaEncodingProfile BuildProfile(ConversionContext context, MediaEncodingProfile? source)
    {
        var options = context.Options;
        var target = context.Target.Id;
        MediaEncodingProfile profile = target switch
        {
            "mp3" => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
            "m4a" => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High),
            "wav" => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High),
            "wma" => MediaEncodingProfile.CreateWma(AudioEncodingQuality.High),
            "flac" => MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High),
            "mp4" => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p),
            "wmv" => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.HD1080p),
            _ => throw new ConversionException($"Windows Media Foundation cannot produce {context.Target.Name}."),
        };

        if (profile.Audio is not null)
        {
            var bitrate = options.GetInt("bitrate", 0);
            if (bitrate > 0 && target is not "wav" and not "flac")
            {
                profile.Audio.Bitrate = (uint)bitrate * 1000;
            }

            var sampleRate = options.GetInt("sampleRate", 0);
            if (sampleRate > 0)
            {
                profile.Audio.SampleRate = (uint)sampleRate;
            }
            else if (source?.Audio is { SampleRate: > 0 } sourceAudio && target is "wav" or "flac")
            {
                profile.Audio.SampleRate = sourceAudio.SampleRate;
            }

            var channels = options.GetInt("channels", 0);
            if (channels > 0)
            {
                profile.Audio.ChannelCount = (uint)channels;
            }
            else if (source?.Audio is { ChannelCount: > 0 and <= 2 } sourceChannels)
            {
                profile.Audio.ChannelCount = sourceChannels.ChannelCount;
            }

            if (target == "wav")
            {
                // PCM: the byte rate must match the sample format exactly or Media Foundation rejects the profile.
                var bits = profile.Audio.BitsPerSample == 0 ? 16u : profile.Audio.BitsPerSample;
                profile.Audio.BitsPerSample = bits;
                profile.Audio.Bitrate = profile.Audio.SampleRate * profile.Audio.ChannelCount * bits;
            }
        }

        if (profile.Video is not null)
        {
            if (source?.Video is { Width: > 0, Height: > 0 } sourceVideo)
            {
                var maxHeight = options.GetInt("height", 0);
                var (width, height) = FitHeight(sourceVideo.Width, sourceVideo.Height, maxHeight);
                profile.Video.Width = width;
                profile.Video.Height = height;
                if (sourceVideo.FrameRate.Numerator > 0 && sourceVideo.FrameRate.Denominator > 0)
                {
                    profile.Video.FrameRate.Numerator = sourceVideo.FrameRate.Numerator;
                    profile.Video.FrameRate.Denominator = sourceVideo.FrameRate.Denominator;
                }

                // Bits per pixel per frame: ~0.1 is high quality H.264, ~0.05 balanced.
                var fps = sourceVideo.FrameRate.Denominator > 0 ? (double)sourceVideo.FrameRate.Numerator / sourceVideo.FrameRate.Denominator : 30;
                var bitsPerPixel = options.Get("quality", "balanced") switch
                {
                    "high" => 0.12,
                    "small" => 0.04,
                    _ => 0.07,
                };
                profile.Video.Bitrate = (uint)Math.Clamp(width * height * Math.Clamp(fps, 1, 120) * bitsPerPixel, 300_000, 80_000_000);
            }
            else
            {
                context.AddWarning("The source video size could not be read; the default 1080p profile was used.");
            }

            if (source is not null && source.Audio is null)
            {
                profile.Audio = null;
            }
        }

        return profile;
    }

    /// <summary>Scales down to <paramref name="maxHeight"/> keeping aspect ratio and even dimensions.</summary>
    internal static (uint Width, uint Height) FitHeight(uint width, uint height, int maxHeight)
    {
        if (maxHeight <= 0 || height <= maxHeight)
        {
            return (width & ~1u, height & ~1u);
        }

        var scaledWidth = (uint)Math.Round(width * (double)maxHeight / height);
        return (Math.Max(2, scaledWidth & ~1u), (uint)maxHeight & ~1u);
    }

    internal static OptionDescriptor[] AudioOptions(string target)
    {
        var options = new List<OptionDescriptor>();
        switch (target)
        {
            case "mp3":
                options.Add(OptionDescriptor.Choice("bitrate", "Quality", "192",
                    new("320", "320 kbps", "Highest"), new("256", "256 kbps"), new("192", "192 kbps", "Recommended"), new("128", "128 kbps"), new("96", "96 kbps", "Smallest")));
                break;
            case "m4a":
                options.Add(OptionDescriptor.Choice("bitrate", "Quality", "192",
                    new("192", "192 kbps", "Recommended"), new("160", "160 kbps"), new("128", "128 kbps"), new("96", "96 kbps", "Smallest")));
                break;
            case "wma":
                options.Add(OptionDescriptor.Choice("bitrate", "Quality", "192", new("192", "192 kbps"), new("128", "128 kbps"), new("96", "96 kbps")));
                break;
        }

        options.Add(OptionDescriptor.Choice("sampleRate", "Sample rate", "0",
            new("0", "Same as source"), new("48000", "48 kHz"), new("44100", "44.1 kHz"), new("22050", "22.05 kHz")) with { IsAdvanced = true });
        options.Add(OptionDescriptor.Choice("channels", "Channels", "0",
            new("0", "Same as source"), new("2", "Stereo"), new("1", "Mono")) with { IsAdvanced = true });
        return [.. options];
    }

    internal static readonly OptionDescriptor ResolutionOption = OptionDescriptor.Choice("height", "Resolution", "0",
        new("0", "Same as source"),
        new("2160", "4K (2160p)"),
        new("1080", "1080p"),
        new("720", "720p"),
        new("480", "480p")) with
    {
        Description = "Video is only ever scaled down",
    };

    internal static readonly OptionDescriptor VideoQualityOption = OptionDescriptor.Choice("quality", "Quality", "balanced",
        new("high", "High", "Larger files"),
        new("balanced", "Balanced"),
        new("small", "Small file", "Visible compression on fast motion"));
}
