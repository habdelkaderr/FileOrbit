using System.Runtime.InteropServices.WindowsRuntime;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Formats;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FileOrbit.Engines.Images;

/// <summary>
/// Built-in image engine. Decoding uses Windows Imaging (so installed codec extensions such as HEIF
/// and WebP are honoured); SVG is rendered with Svg.Skia. Encoding uses Windows Imaging for
/// PNG/JPEG/BMP/GIF/TIFF, Skia for WebP and PDF, and FileOrbit's own writer for ICO.
/// </summary>
public sealed class ImageEngine : ConversionEngineBase
{
    public const string EngineId = "images";

    private static readonly string[] RasterSources = ["png", "jpeg", "bmp", "gif", "tiff", "ico", "webp", "heic", "avif"];
    private static readonly string[] Targets = ["png", "jpeg", "webp", "bmp", "gif", "tiff", "ico", "pdf"];

    public override string Id => EngineId;

    public override string DisplayName => "Windows Imaging + Skia";

    public override string Description => "Built-in image conversion using Windows Imaging Component codecs and SkiaSharp. Runs entirely on this PC.";

    protected override EngineStatus CheckStatus()
    {
        var extensions = WindowsImaging.SupportedDecoderExtensions;
        var optional = new List<string>();
        if (extensions.Contains(".heic")) optional.Add("HEIC");
        if (extensions.Contains(".webp")) optional.Add("WebP");
        if (extensions.Contains(".avif")) optional.Add("AVIF");
        var summary = optional.Count > 0 ? $"Ready. Optional Windows codecs found: {string.Join(", ", optional)}" : "Ready. No optional Windows image codecs found";
        return EngineStatus.Available(summary);
    }

    public override CapabilityStatus GetCapabilityStatus(ConversionCapability capability)
    {
        var status = GetStatus();
        if (!status.IsAvailable)
        {
            return status.ToCapabilityStatus();
        }

        return capability.SourceFormatId switch
        {
            "heic" when !WindowsImaging.CanDecode(".heic") =>
                CapabilityStatus.Missing("Reading HEIC needs the free 'HEIF Image Extensions' (and 'HEVC Video Extensions') from the Microsoft Store."),
            "avif" when !WindowsImaging.CanDecode(".avif") =>
                CapabilityStatus.Missing("Reading AVIF needs the free 'AV1 Video Extension' from the Microsoft Store."),
            "webp" when !WindowsImaging.CanDecode(".webp") =>
                CapabilityStatus.Missing("Reading WebP needs the free 'WebP Image Extensions' from the Microsoft Store."),
            _ => CapabilityStatus.Ok,
        };
    }

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var source in RasterSources.Append("svg"))
        {
            foreach (var target in Targets)
            {
                if (source == target)
                {
                    continue;
                }

                yield return Cap(source, target, FidelityFor(source, target), quality: 70) with
                {
                    Note = NoteFor(source, target),
                    Options = OptionsFor(source, target),
                    Cost = source is "heic" or "avif" || target == "webp" ? ResourceCost.Medium : ResourceCost.Low,
                };
            }
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var source = context.Input.Format.Id;
        var target = context.Target.Id;
        var options = context.Options;
        var maxSize = ParseMaxSize(options.Get("maxSize", "original"));
        var metadataMode = MetadataModeFrom(options);

        context.Report(0.05, "Reading image");
        if (source == "svg" && target == "pdf")
        {
            await Task.Run(() => SkiaImaging.WriteSvgPdf(context.Input.Path, context.PrimaryOutputPath), cancellationToken);
            context.Report(1);
            return;
        }

        var image = source == "svg"
            ? await Task.Run(() => SkiaImaging.RenderSvg(context.Input.Path, ParseMaxSize(options.Get("svgWidth", "1024"))), cancellationToken)
            : await WindowsImaging.DecodeAsync(context.Input.Path, maxSize, metadataMode != MetadataMode.Remove, cancellationToken);

        if (image.SourceFrameCount > 1)
        {
            context.AddWarning($"The source has {image.SourceFrameCount} frames or pages; only the first was converted.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        context.Report(0.5, "Encoding");
        await SaveAsync(image, context, context.PrimaryOutputPath, metadataMode, cancellationToken);
        context.Report(1, "Done");
    }

    /// <summary>
    /// Encodes <paramref name="image"/> as <see cref="ConversionContext.Target"/> using the context's
    /// quality options, adding user-facing warnings for anything the format cannot keep.
    /// Shared by conversions, edits and background removal so every output is written the same way.
    /// </summary>
    internal static async Task SaveAsync(RasterImage image, ConversionContext context, string output, MetadataMode metadataMode, CancellationToken cancellationToken, int? qualityOverride = null)
    {
        var options = context.Options;
        var target = context.Target.Id;
        var quality = qualityOverride ?? options.GetInt("quality", 90);
        switch (target)
        {
            case "webp":
                await Task.Run(() => SkiaImaging.EncodeWebP(image, output, quality, options.GetBool("lossless", false)), cancellationToken);
                if (image.Metadata.Count > 0 && metadataMode != MetadataMode.Remove)
                {
                    context.AddWarning("Photo metadata is not copied into WebP files.");
                }

                break;
            case "pdf":
                await Task.Run(() => SkiaImaging.WriteImagePdf(image, output), cancellationToken);
                break;
            case "ico":
                await WriteIcoAsync(image, output, cancellationToken);
                break;
            default:
                if (target is "jpeg" or "bmp" && image.HasTransparency)
                {
                    context.AddWarning($"{context.Target.Name} does not support transparency; transparent areas were filled with white.");
                }

                if (target == "gif")
                {
                    context.AddWarning("GIF is limited to 256 colours, so photos may show banding.");
                }

                var keptMetadata = await WindowsImaging.EncodeAsync(image, target, output, quality / 100.0, metadataMode, cancellationToken);
                if (!keptMetadata && image.Metadata.Count > 0)
                {
                    context.AddWarning($"Photo metadata could not be stored in {context.Target.Name}.");
                }

                break;
        }
    }

    internal static MetadataMode MetadataModeFrom(ConversionOptions options) => options.Get("metadata", "keep") switch
    {
        "nolocation" => MetadataMode.KeepWithoutLocation,
        "remove" => MetadataMode.Remove,
        _ => MetadataMode.Keep,
    };

    private static async Task WriteIcoAsync(RasterImage image, string output, CancellationToken cancellationToken)
    {
        var square = image.PadToSquare();
        var sizes = IcoWriter.StandardSizes.Where(s => s <= Math.Max(square.Width, 16) || s <= 48).ToList();
        var entries = new List<(int, byte[])>();
        foreach (var size in sizes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add((size, await EncodePngBytesAsync(square, size, cancellationToken)));
        }

        IcoWriter.Write(output, entries);
    }

    private static async Task<byte[]> EncodePngBytesAsync(RasterImage image, int size, CancellationToken cancellationToken)
    {
        using var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)image.Width, (uint)image.Height, 96, 96, image.Pixels);
        encoder.BitmapTransform.ScaledWidth = (uint)size;
        encoder.BitmapTransform.ScaledHeight = (uint)size;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync().AsTask(cancellationToken);

        var bytes = new byte[memory.Size];
        memory.Seek(0);
        await memory.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken);
        return bytes;
    }

    private static Fidelity FidelityFor(string source, string target) => target switch
    {
        "jpeg" or "webp" => Fidelity.Lossy,
        "gif" => Fidelity.Lossy,
        _ when source == "svg" => Fidelity.Rasterized,
        _ => Fidelity.Lossless,
    };

    private static string? NoteFor(string source, string target) => (source, target) switch
    {
        ("svg", "pdf") => "Kept as vector graphics. External images referenced by the SVG are not loaded",
        ("svg", _) => "Rendered at the chosen width. External images referenced by the SVG are not loaded",
        (_, "ico") => "Creates a multi-size icon (16–256 px)",
        (_, "pdf") => "One page sized to the image",
        (_, "gif") => "Limited to 256 colours; animation is not created",
        ("gif", _) => "Only the first frame of animated GIFs is converted",
        ("tiff", _) => "Only the first page of multi-page TIFFs is converted",
        _ => null,
    };

    private static OptionDescriptor[] OptionsFor(string source, string target)
    {
        var options = new List<OptionDescriptor>();
        if (source == "svg")
        {
            if (target != "pdf")
            {
                options.Add(OptionDescriptor.Choice("svgWidth", "Width", "1024",
                    new("256", "256 px"), new("512", "512 px"), new("1024", "1024 px"), new("2048", "2048 px"), new("4096", "4096 px")));
            }
        }
        else if (target is not "ico" and not "pdf")
        {
            options.Add(MaxSizeOption("original", includeOriginal: true));
        }

        options.AddRange(QualityOptions(target));
        if (source != "svg" && target is not "ico" and not "pdf")
        {
            options.Add(MetadataOption);
        }

        return [.. options];
    }

    internal static IEnumerable<OptionDescriptor> QualityOptions(string target)
    {
        if (target is "jpeg" or "webp")
        {
            yield return QualityOption("90");
        }

        if (target == "webp")
        {
            yield return OptionDescriptor.Toggle("lossless", "Lossless WebP", false, "Larger files with no quality loss") with { IsAdvanced = true };
        }
    }

    internal static OptionDescriptor QualityOption(string defaultValue) =>
        OptionDescriptor.Choice("quality", "Quality", defaultValue,
            new("95", "Maximum", "Largest files"),
            new("90", "High"),
            new("80", "Balanced"),
            new("65", "Small", "Noticeable artefacts on detailed photos"),
            new("45", "Smallest"));

    internal static OptionDescriptor MaxSizeOption(string defaultValue, bool includeOriginal)
    {
        var choices = new List<OptionChoice>();
        if (includeOriginal)
        {
            choices.Add(new("original", "Original size"));
        }

        choices.AddRange([
            new("3840", "3840 px (4K)"),
            new("2560", "2560 px"),
            new("1920", "1920 px (Full HD)"),
            new("1280", "1280 px"),
            new("800", "800 px"),
            new("400", "400 px"),
        ]);
        return OptionDescriptor.Choice("maxSize", "Longest side", defaultValue, [.. choices]) with
        {
            Description = "Images are only ever scaled down, never enlarged",
        };
    }

    internal static readonly OptionDescriptor MetadataOption = OptionDescriptor.Choice("metadata", "Photo metadata", "keep",
        new("keep", "Keep", "Camera, date and location where the format supports it"),
        new("nolocation", "Remove location", "Keep camera and date, drop GPS position"),
        new("remove", "Remove all")) with
    {
        IsAdvanced = true,
    };

    internal static int? ParseMaxSize(string value) => int.TryParse(value, out var size) && size > 0 ? size : null;
}
