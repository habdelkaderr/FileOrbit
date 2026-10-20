using System.Globalization;
using System.Text.RegularExpressions;
using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Processes;

namespace FileOrbit.Engines.Media;

/// <summary>
/// Audio and video conversion with an installed FFmpeg. FFmpeg is not bundled (its licence depends
/// on the build); FileOrbit detects it on PATH, in common install folders, or at a configured path.
/// </summary>
public sealed partial class FfmpegEngine : ConversionEngineBase
{
    public const string EngineId = "ffmpeg";

    private static readonly string[] AudioFormats = ["mp3", "wav", "flac", "m4a", "aac", "ogg", "opus", "wma"];
    private static readonly string[] AudioTargets = ["mp3", "wav", "flac", "m4a", "ogg", "opus", "wma"];
    private static readonly string[] VideoFormats = ["mp4", "mkv", "mov", "webm", "avi", "wmv"];
    private static readonly string[] VideoTargets = ["mp4", "mkv", "mov", "webm", "avi", "gif"];

    private readonly ExecutableLocator _locator;
    private string? _executable;

    public FfmpegEngine(ExecutableLocator locator) => _locator = locator;

    public override string Id => EngineId;

    public override string DisplayName => "FFmpeg";

    public override string Description => "Converts almost any audio or video format using an installed copy of FFmpeg.";

    public override bool IsExternal => true;

    public override DependencyInfo Dependency { get; } = new(
        "FFmpeg",
        "MKV, WebM, MOV, AVI, OGG and Opus conversion, GIF creation, and codecs Windows lacks",
        "LGPL 2.1+ or GPL 2+ depending on the build",
        "https://ffmpeg.org/download.html",
        WingetId: "Gyan.FFmpeg",
        InstallNotes: "After installing, restart FileOrbit or press Refresh so it can find ffmpeg.exe.");

    protected override EngineStatus CheckStatus()
    {
        _executable = _locator.Find(Id, "ffmpeg.exe",
        [
            @"%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe",
            @"%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\ffmpeg-*\bin\ffmpeg.exe",
            @"%LOCALAPPDATA%\Microsoft\WinGet\Packages\BtbN.FFmpeg*\ffmpeg-*\bin\ffmpeg.exe",
            @"%ProgramFiles%\ffmpeg\bin\ffmpeg.exe",
            @"%SystemDrive%\ffmpeg\bin\ffmpeg.exe",
            @"%USERPROFILE%\scoop\shims\ffmpeg.exe",
            @"%ChocolateyInstall%\bin\ffmpeg.exe",
        ]);
        return _executable is null
            ? EngineStatus.Unavailable("FFmpeg is not installed.")
            : EngineStatus.Available("Ready", location: _executable);
    }

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var source in AudioFormats.Concat(VideoFormats).Append("gif"))
        {
            var isVideo = VideoFormats.Contains(source) || source == "gif";
            foreach (var target in AudioTargets)
            {
                if (target == source || source == "gif")
                {
                    continue;
                }

                yield return Cap(source, target, target is "wav" or "flac" ? Fidelity.Lossless : Fidelity.Lossy, quality: 80) with
                {
                    Note = isVideo ? "Extracts the audio track" : null,
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
                yield return Cap(source, target, Fidelity.Lossy, quality: 80) with
                {
                    Note = target switch
                    {
                        "gif" => "Animated GIF at reduced frame rate and size; no audio",
                        "webm" => "VP9 video with Opus audio; encoding is slow",
                        "avi" => "Legacy format (MPEG-4 Part 2); prefer MP4 for new files",
                        _ => "H.264 video with AAC audio",
                    },
                    Options = target == "gif" ? [GifWidthOption, GifFpsOption] : VideoOptions(source, target),
                    Cost = ResourceCost.High,
                };
            }
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var executable = (GetStatus().IsAvailable ? _executable : null) ?? throw new ConversionException("FFmpeg is not installed.");
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-n", "-i", context.Input.Path };
        arguments.AddRange(BuildCodecArguments(context));
        arguments.AddRange(["-progress", "pipe:1", "-nostats", context.PrimaryOutputPath]);

        TimeSpan? duration = null;
        context.Report(null, "Starting FFmpeg");
        var result = await ProcessRunner.RunAsync(executable, arguments, cancellationToken,
            onStandardOutputLine: line =>
            {
                if (duration is { TotalMilliseconds: > 0 } total && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                {
                    context.Report(micros / 1000.0 / total.TotalMilliseconds, "Converting");
                }
            },
            onStandardErrorLine: line =>
            {
                if (duration is null)
                {
                    var match = DurationPattern().Match(line);
                    if (match.Success && TimeSpan.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var parsed))
                    {
                        duration = parsed;
                    }
                }
            },
            log: context.Log);

        if (result.ExitCode != 0)
        {
            var tail = result.ErrorTail();
            var message = tail.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) || tail.Contains("Encoder not found", StringComparison.OrdinalIgnoreCase)
                ? "This FFmpeg build does not include the required encoder. Install a full build (e.g. Gyan.FFmpeg)."
                : tail.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase) || tail.Contains("Output file #0 does not contain any stream", StringComparison.OrdinalIgnoreCase)
                    ? "The file has no audio track to extract."
                    : tail.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)
                        ? "FFmpeg could not read the file. It may be damaged."
                        : "FFmpeg could not convert the file.";
            throw new ConversionException(message, tail);
        }

        context.Report(1, "Done");
    }

    internal static IEnumerable<string> BuildCodecArguments(ConversionContext context)
    {
        var options = context.Options;
        var target = context.Target.Id;
        var bitrate = options.GetInt("bitrate", 192).ToString(CultureInfo.InvariantCulture) + "k";
        var audioExtras = new List<string>();
        if (options.GetInt("sampleRate", 0) is > 0 and var rate)
        {
            audioExtras.AddRange(["-ar", rate.ToString(CultureInfo.InvariantCulture)]);
        }

        if (options.GetInt("channels", 0) is > 0 and var channels)
        {
            audioExtras.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);
        }

        // Video file → audio target: drop the video stream ("-vn") and any embedded cover art.
        IEnumerable<string> Audio(params string[] codec) => ["-vn", "-map_metadata", "0", .. codec, .. audioExtras];

        switch (target)
        {
            case "mp3": return Audio("-c:a", "libmp3lame", "-b:a", bitrate);
            case "m4a": return Audio("-c:a", "aac", "-b:a", bitrate);
            case "wav": return Audio("-c:a", "pcm_s16le");
            case "flac": return Audio("-c:a", "flac");
            case "ogg": return Audio("-c:a", "libvorbis", "-q:a", options.Get("vorbisQuality", "6"));
            case "opus": return Audio("-c:a", "libopus", "-b:a", bitrate);
            case "wma": return Audio("-c:a", "wmav2", "-b:a", bitrate);
            case "gif":
            {
                var width = options.GetInt("gifWidth", 480);
                var fps = options.GetInt("gifFps", 12);
                return ["-an", "-vf", $"fps={fps},scale={width}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4", "-loop", "0"];
            }
        }

        if (options.GetBool("copy", false) && target is "mp4" or "mkv" or "mov")
        {
            return ["-map", "0", "-c", "copy", .. (target == "mp4" ? new[] { "-movflags", "+faststart" } : [])];
        }

        var crf = options.Get("quality", "balanced") switch
        {
            "high" => 18,
            "small" => 28,
            _ => 23,
        };
        var height = options.GetInt("height", 0);
        var scale = height > 0 ? new[] { "-vf", $"scale=-2:'min(ih,{height})'" } : [];
        return target switch
        {
            "webm" => ["-c:v", "libvpx-vp9", "-crf", (crf + 8).ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-row-mt", "1", .. scale, "-c:a", "libopus", "-b:a", "128k"],
            "avi" => ["-c:v", "mpeg4", "-q:v", crf <= 18 ? "2" : crf <= 23 ? "4" : "7", .. scale, "-c:a", "libmp3lame", "-b:a", "192k"],
            _ => ["-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", .. scale, "-c:a", "aac", "-b:a", "160k",
                  .. (target is "mp4" or "mov" ? new[] { "-movflags", "+faststart" } : [])],
        };
    }

    private static OptionDescriptor[] AudioOptions(string target)
    {
        var options = new List<OptionDescriptor>();
        if (target is "mp3" or "m4a" or "opus" or "wma")
        {
            var choices = target == "opus"
                ? new OptionChoice[] { new("192", "192 kbps"), new("128", "128 kbps", "Recommended"), new("96", "96 kbps"), new("64", "64 kbps", "Speech") }
                : [new("320", "320 kbps", "Highest"), new("256", "256 kbps"), new("192", "192 kbps", "Recommended"), new("128", "128 kbps"), new("96", "96 kbps", "Smallest")];
            options.Add(OptionDescriptor.Choice("bitrate", "Quality", target == "opus" ? "128" : "192", choices));
        }

        if (target == "ogg")
        {
            options.Add(OptionDescriptor.Choice("vorbisQuality", "Quality", "6", new("8", "High"), new("6", "Balanced"), new("4", "Small")));
        }

        options.Add(OptionDescriptor.Choice("sampleRate", "Sample rate", "0",
            new("0", "Same as source"), new("48000", "48 kHz"), new("44100", "44.1 kHz"), new("22050", "22.05 kHz")) with { IsAdvanced = true });
        options.Add(OptionDescriptor.Choice("channels", "Channels", "0",
            new("0", "Same as source"), new("2", "Stereo"), new("1", "Mono")) with { IsAdvanced = true });
        return [.. options];
    }

    private static OptionDescriptor[] VideoOptions(string source, string target)
    {
        var options = new List<OptionDescriptor> { MediaFoundationEngine.ResolutionOption, MediaFoundationEngine.VideoQualityOption };
        if (target is "mp4" or "mkv" or "mov" && source is "mp4" or "mkv" or "mov")
        {
            options.Add(OptionDescriptor.Toggle("copy", "Copy streams without re-encoding", false,
                "Very fast and lossless, but only works when the codecs are allowed in the new container"));
        }

        return [.. options];
    }

    private static readonly OptionDescriptor GifWidthOption = OptionDescriptor.Choice("gifWidth", "Width", "480",
        new("320", "320 px"), new("480", "480 px"), new("640", "640 px"), new("800", "800 px"));

    private static readonly OptionDescriptor GifFpsOption = OptionDescriptor.Choice("gifFps", "Frame rate", "12",
        new("8", "8 fps", "Smallest"), new("12", "12 fps"), new("15", "15 fps"), new("24", "24 fps", "Smoothest, large"));

    [GeneratedRegex(@"Duration:\s*(\d+:\d{2}:\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationPattern();
}
