using System.Globalization;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Formats;

namespace FileOrbit.Engines.Images;

/// <summary>
/// Built-in image edits: resize, compress to a target size, rotate/flip, crop to an aspect ratio,
/// trim borders, black and white, and auto-enhance. Each edit saves a new file next to the
/// original ("photo (resized).jpg"); the original is never changed.
/// </summary>
public sealed class ImageEditEngine : ConversionEngineBase
{
    public const string EngineId = "image-edit";

    /// <summary>Sources and the format each edit is saved in (formats Windows can't write are saved as JPEG/PNG).</summary>
    private static readonly Dictionary<string, string> EditTargets = new()
    {
        ["png"] = "png",
        ["jpeg"] = "jpeg",
        ["webp"] = "webp",
        ["bmp"] = "bmp",
        ["tiff"] = "tiff",
        ["gif"] = "png",
        ["heic"] = "jpeg",
        ["avif"] = "jpeg",
    };

    public override string Id => EngineId;

    public override string DisplayName => "Image editor";

    public override string Description => "Resize, compress, rotate, crop, trim, black and white and auto-enhance. Built in; runs on this PC.";

    protected override EngineStatus CheckStatus() => EngineStatus.Available("Ready (built in)");

    public override CapabilityStatus GetCapabilityStatus(ConversionCapability capability) =>
        capability.SourceFormatId is "heic" or "avif" or "webp" && !WindowsImaging.CanDecode("." + capability.SourceFormatId)
            ? CapabilityStatus.Missing($"Reading {capability.SourceFormatId.ToUpperInvariant()} needs the matching image extension from the Microsoft Store.")
            : CapabilityStatus.Ok;

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var (source, target) in EditTargets)
        {
            var lossy = target is "jpeg" or "webp";
            var saveNote = source == target ? null : $" Saved as {target.ToUpperInvariant()}";
            ConversionCapability Edit(string variant, string label, string suffix, string note, params OptionDescriptor[] options) =>
                Cap(source, target, lossy ? Fidelity.Lossy : Fidelity.Lossless, quality: 70) with
                {
                    Action = ActionKind.Transform,
                    Variant = variant,
                    Label = label,
                    OutputSuffix = suffix,
                    Note = note + (saveNote ?? string.Empty),
                    Options = [.. options, .. ImageEngine.QualityOptions(target), ImageEngine.MetadataOption],
                };

            yield return Edit("resized", "Resize", "resized", "Change the size by side, exact dimensions or percentage", ResizeOptions);
            yield return Edit("rotated", "Rotate", "rotated", "Rotate or flip the picture", RotateOption);
            yield return Edit("cropped", "Crop", "cropped", "Crop to a shape such as square or 16:9", AspectOption, AnchorOption);
            yield return Edit("trimmed", "Trim borders", "trimmed", "Removes plain or transparent borders around the picture", ToleranceOption);
            yield return Edit("monochrome", "Black & white", "black and white", "Black and white, sepia or high-contrast", MonochromeOption);
            yield return Edit("enhanced", "Auto enhance", "enhanced", "Automatically fixes dull or slightly tinted photos", StrengthOption);
            if (lossy)
            {
                yield return Edit("compressed", "Compress", "compressed", "Makes the file smaller, for example to fit an email or upload limit", CompressOption)
                    with { Options = [CompressOption, ImageEngine.MetadataOption] };
            }
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var options = context.Options;
        var metadataMode = ImageEngine.MetadataModeFrom(options);
        context.Report(0.05, "Reading image");
        var image = await WindowsImaging.DecodeAsync(context.Input.Path, null, metadataMode != MetadataMode.Remove, cancellationToken);
        if (image.SourceFrameCount > 1)
        {
            context.AddWarning($"The source has {image.SourceFrameCount} frames or pages; only the first was edited.");
        }

        context.Report(0.4, "Editing");
        if (context.Capability.Variant == "compressed")
        {
            await CompressAsync(image, context, metadataMode, cancellationToken);
            return;
        }

        var edited = await Task.Run(() => Apply(image, context.Capability.Variant!, options, context), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        context.Report(0.7, "Saving");
        await ImageEngine.SaveAsync(edited, context, context.PrimaryOutputPath, metadataMode, cancellationToken);
        context.Report(1, "Done");
    }

    internal static RasterImage Apply(RasterImage image, string variant, ConversionOptions options, ConversionContext? context = null)
    {
        switch (variant)
        {
            case "resized":
            {
                var mode = options.Get("mode", "longest") switch
                {
                    "width" => ResizeMode.Width,
                    "height" => ResizeMode.Height,
                    "exact" => ResizeMode.Exact,
                    "percent" => ResizeMode.Percent,
                    _ => ResizeMode.LongestSide,
                };
                var fit = options.Get("fit", "contain") switch
                {
                    "cover" => ResizeFit.Cover,
                    "stretch" => ResizeFit.Stretch,
                    _ => ResizeFit.Contain,
                };
                var allowUpscale = options.GetBool("upscale", false);
                var plan = ImageOps.PlanResize(image.Width, image.Height, mode, options.GetInt("size", 1920), options.GetInt("width", 1920),
                    options.GetInt("height", 1080), options.GetInt("percent", 50), fit, allowUpscale);
                if (!allowUpscale && plan.ScaledWidth == image.Width && plan.ScaledHeight == image.Height && !plan.NeedsCrop)
                {
                    context?.AddWarning("The picture is already that size or smaller, so it was not enlarged. Turn on “Allow enlarging” to make it bigger.");
                }

                var resized = ImageOps.Resize(image, plan.ScaledWidth, plan.ScaledHeight);
                return plan.NeedsCrop
                    ? ImageOps.Crop(resized, (plan.ScaledWidth - plan.CropWidth) / 2, (plan.ScaledHeight - plan.CropHeight) / 2, plan.CropWidth, plan.CropHeight)
                    : resized;
            }

            case "rotated":
                return options.Get("rotate", "right") switch
                {
                    "left" => ImageOps.RotateCounterClockwise(image),
                    "180" => ImageOps.Rotate180(image),
                    "fliph" => ImageOps.FlipHorizontal(image),
                    "flipv" => ImageOps.FlipVertical(image),
                    _ => ImageOps.RotateClockwise(image),
                };

            case "cropped":
            {
                var parts = options.Get("aspect", "1:1").Split(':');
                var aspect = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
                return ImageOps.CropToAspect(image, aspect, options.Get("anchor", "center"));
            }

            case "trimmed":
            {
                var trimmed = ImageOps.Trim(image, options.GetInt("tolerance", 24));
                if (trimmed.Width == image.Width && trimmed.Height == image.Height)
                {
                    context?.AddWarning("No plain border was found, so the picture was not trimmed.");
                }

                return trimmed;
            }

            case "monochrome":
                return ImageOps.Monochrome(image, options.Get("style", "gray"));

            case "enhanced":
                return ImageOps.AutoEnhance(image, options.GetInt("strength", 100) / 100.0);

            default:
                throw new ConversionException($"Unknown image edit '{variant}'.");
        }
    }

    /// <summary>Re-encodes at a quality preset, or searches for the best quality that fits a size limit.</summary>
    private static async Task CompressAsync(RasterImage image, ConversionContext context, MetadataMode metadataMode, CancellationToken cancellationToken)
    {
        var goal = context.Options.Get("goal", "q65");
        if (goal.StartsWith('q'))
        {
            await ImageEngine.SaveAsync(image, context, context.PrimaryOutputPath, metadataMode, cancellationToken, int.Parse(goal[1..], CultureInfo.InvariantCulture));
            context.Report(1, "Done");
            return;
        }

        var limit = long.Parse(goal[3..], CultureInfo.InvariantCulture) * 1024; // "max500" -> 500 KB
        var probe = Path.Combine(context.StagingDirectory, ".probe" + context.Target.PrimaryExtension);
        var candidate = image;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            // Binary search the quality that fits; if even low quality is too big, shrink the picture and retry.
            int low = 30, high = 92, best = -1;
            while (low <= high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var quality = (low + high) / 2;
                context.Report(0.4 + 0.05 * attempt, $"Trying quality {quality}");
                File.Delete(probe);
                await ImageEngine.SaveAsync(candidate, context, probe, metadataMode, cancellationToken, quality);
                if (new FileInfo(probe).Length <= limit)
                {
                    best = quality;
                    low = quality + 1;
                }
                else
                {
                    high = quality - 1;
                }
            }

            File.Delete(probe);
            if (best > 0)
            {
                await ImageEngine.SaveAsync(candidate, context, context.PrimaryOutputPath, metadataMode, cancellationToken, best);
                if (candidate.Width != image.Width)
                {
                    context.AddWarning($"To fit the size limit the picture was also reduced to {candidate.Width}×{candidate.Height} pixels.");
                }

                context.Report(1, "Done");
                return;
            }

            candidate = ImageOps.Resize(candidate, Math.Max(16, (int)(candidate.Width * 0.75)), Math.Max(16, (int)(candidate.Height * 0.75)));
        }

        throw new ConversionException("The picture could not be made small enough. Try a larger size limit.");
    }

    private static readonly OptionDescriptor[] ResizeOptions =
    [
        OptionDescriptor.Choice("mode", "Resize by", "longest",
            new("longest", "Longest side"), new("width", "Width"), new("height", "Height"),
            new("exact", "Exact width × height"), new("percent", "Percentage")),
        OptionDescriptor.Integer("size", "Pixels", 1920, 16, ImageOps.MaxDimension).ShownWhen("mode", "longest", "width", "height"),
        OptionDescriptor.Integer("width", "Width (px)", 1920, 16, ImageOps.MaxDimension).ShownWhen("mode", "exact"),
        OptionDescriptor.Integer("height", "Height (px)", 1080, 16, ImageOps.MaxDimension).ShownWhen("mode", "exact"),
        OptionDescriptor.Choice("fit", "Fit", "contain",
            new("contain", "Fit inside (keep proportions)"), new("cover", "Fill and crop"), new("stretch", "Stretch")).ShownWhen("mode", "exact"),
        OptionDescriptor.Choice("percent", "Percentage", "50",
            new("25", "25%"), new("50", "50%"), new("75", "75%"), new("150", "150%"), new("200", "200%")).ShownWhen("mode", "percent"),
        OptionDescriptor.Toggle("upscale", "Allow enlarging", false, "Enlarging can't add detail; large increases look soft"),
    ];

    private static readonly OptionDescriptor RotateOption = OptionDescriptor.Choice("rotate", "Rotate", "right",
        new("right", "90° right"), new("left", "90° left"), new("180", "180°"),
        new("fliph", "Flip horizontally (mirror)"), new("flipv", "Flip vertically"));

    private static readonly OptionDescriptor AspectOption = OptionDescriptor.Choice("aspect", "Shape", "1:1",
        new("1:1", "Square (1:1)"), new("4:5", "Portrait (4:5)"), new("3:4", "Portrait (3:4)"), new("9:16", "Tall (9:16)"),
        new("4:3", "Landscape (4:3)"), new("3:2", "Photo (3:2)"), new("16:9", "Widescreen (16:9)"));

    private static readonly OptionDescriptor AnchorOption = OptionDescriptor.Choice("anchor", "Keep", "center",
        new("center", "Centre"), new("top", "Top"), new("bottom", "Bottom"), new("left", "Left"), new("right", "Right"));

    private static readonly OptionDescriptor ToleranceOption = OptionDescriptor.Choice("tolerance", "Sensitivity", "24",
        new("8", "Exact colour only"), new("24", "Normal"), new("60", "Loose (scanned pages, noisy edges)"));

    private static readonly OptionDescriptor MonochromeOption = OptionDescriptor.Choice("style", "Style", "gray",
        new("gray", "Black & white"), new("sepia", "Sepia"), new("contrast", "High-contrast black & white"));

    private static readonly OptionDescriptor StrengthOption = OptionDescriptor.Choice("strength", "Strength", "100",
        new("50", "Gentle"), new("100", "Normal"), new("150", "Strong"));

    private static readonly OptionDescriptor CompressOption = OptionDescriptor.Choice("goal", "Target", "max500",
        new("max200", "Under 200 KB"), new("max500", "Under 500 KB"), new("max1024", "Under 1 MB"), new("max2048", "Under 2 MB"),
        new("q80", "Good quality"), new("q65", "Smaller file"), new("q45", "Smallest file"));
}
