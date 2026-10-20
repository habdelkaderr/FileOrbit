using System.Runtime.InteropServices;
using FileOrbit.Core.Capabilities;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FileOrbit.Engines.Images;

/// <summary>
/// Removes the background of a picture with the U²-Net-P salient-object model (Apache-2.0), run
/// locally by ONNX Runtime on the CPU. The model is embedded in FileOrbit, so this works offline and
/// pictures never leave the PC. Best for photos with one clear subject (people, products, animals).
/// </summary>
public sealed class BackgroundRemovalEngine : ConversionEngineBase
{
    public const string EngineId = "background-removal";
    private const string ModelResource = "FileOrbit.Models.u2netp.onnx";
    private const int ModelSize = 320;

    /// <summary>Decode at most this size; the mask is computed at 320 px and scaled up anyway.</summary>
    private const int MaxWorkingDimension = 6000;

    private static readonly string[] Sources = ["png", "jpeg", "webp", "bmp", "tiff", "gif", "heic", "avif"];
    private static readonly Lazy<InferenceSession> Session = new(CreateSession, LazyThreadSafetyMode.ExecutionAndPublication);

    public override string Id => EngineId;

    public override string DisplayName => "Background remover (AI)";

    public override string Description => "Cuts out the main subject of a photo with a small AI model that runs on this PC. No internet needed.";

    protected override EngineStatus CheckStatus() =>
        typeof(BackgroundRemovalEngine).Assembly.GetManifestResourceInfo(ModelResource) is null
            ? EngineStatus.Unavailable("The background-removal model is missing from this FileOrbit build.")
            : EngineStatus.Available("Ready (built-in model, runs on the CPU)");

    public override CapabilityStatus GetCapabilityStatus(ConversionCapability capability)
    {
        var status = GetStatus();
        if (!status.IsAvailable)
        {
            return status.ToCapabilityStatus();
        }

        return capability.SourceFormatId is "heic" or "avif" or "webp" && !WindowsImaging.CanDecode("." + capability.SourceFormatId)
            ? CapabilityStatus.Missing($"Reading {capability.SourceFormatId.ToUpperInvariant()} needs the matching image extension from the Microsoft Store.")
            : CapabilityStatus.Ok;
    }

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var source in Sources)
        {
            yield return Cap(source, "png", Fidelity.Lossless, quality: 70) with
            {
                Action = ActionKind.Transform,
                Variant = "nobg",
                Label = "Remove background",
                OutputSuffix = "no background",
                Note = "AI cut-out of the main subject, saved as PNG. Works best with one clear subject; check the edges, especially hair",
                Cost = ResourceCost.High,
                Options =
                [
                    OptionDescriptor.Choice("background", "New background", "transparent",
                        new("transparent", "Transparent"), new("white", "White"), new("black", "Black")),
                    OptionDescriptor.Choice("edges", "Edges", "soft",
                        new("soft", "Soft (natural)"), new("sharp", "Sharp (cut-out look)")),
                ],
            };
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        context.Report(0.05, "Reading image");
        var image = await WindowsImaging.DecodeAsync(context.Input.Path, MaxWorkingDimension, readMetadata: false, cancellationToken);

        context.Report(0.2, "Finding the subject");
        var mask = await Task.Run(() => PredictMask(image, context.Options.Get("edges", "soft") == "sharp"), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var coverage = mask.Average();
        if (coverage < 0.01)
        {
            throw new ConversionException("No clear subject was found in this picture, so the background could not be separated.");
        }

        if (coverage > 0.97)
        {
            context.AddWarning("The model treated almost the whole picture as the subject; little or no background was removed.");
        }

        context.Report(0.8, "Removing background");
        var cutout = ImageOps.ApplyMask(image, mask);
        var background = context.Options.Get("background", "transparent");
        if (background != "transparent")
        {
            cutout = background == "black" ? cutout.FlattenOnto(0, 0, 0) : cutout.FlattenOnto();
        }

        await ImageEngine.SaveAsync(cutout, context, context.PrimaryOutputPath, MetadataMode.Remove, cancellationToken);
        context.Report(1, "Done");
    }

    /// <summary>Returns a per-pixel foreground probability (0..1) at the image's own size.</summary>
    internal static float[] PredictMask(RasterImage image, bool sharpEdges)
    {
        var input = PrepareInput(image);
        var session = Session.Value;
        var inputName = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        var output = results.First().AsTensor<float>();

        // Normalise the first (fused) output map to 0..1, as the U²-Net reference implementation does.
        var raw = new float[ModelSize * ModelSize];
        for (var y = 0; y < ModelSize; y++)
        {
            for (var x = 0; x < ModelSize; x++)
            {
                raw[y * ModelSize + x] = output[0, 0, y, x];
            }
        }

        var min = raw.Min();
        var max = raw.Max();
        var range = Math.Max(max - min, 1e-6f);

        // Scale the 320×320 mask to the image size with smooth interpolation.
        using var small = new SKBitmap(new SKImageInfo(ModelSize, ModelSize, SKColorType.Gray8, SKAlphaType.Opaque));
        var bytes = new byte[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            bytes[i] = (byte)Math.Clamp((raw[i] - min) / range * 255f, 0f, 255f);
        }

        Marshal.Copy(bytes, 0, small.GetPixels(), bytes.Length);
        small.NotifyPixelsChanged();
        using var large = small.Resize(new SKImageInfo(image.Width, image.Height, SKColorType.Gray8, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell))
                          ?? throw new ConversionException("The mask could not be scaled to the picture size.");
        var scaled = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(large.GetPixels() + y * large.RowBytes, scaled, y * image.Width, image.Width);
        }

        var mask = new float[scaled.Length];
        for (var i = 0; i < scaled.Length; i++)
        {
            var value = scaled[i] / 255f;
            // Soft: keep the model's gradual edge but clean up faint haze. Sharp: hard threshold with a short ramp.
            mask[i] = sharpEdges
                ? Math.Clamp((value - 0.45f) / 0.1f, 0f, 1f)
                : Math.Clamp((value - 0.05f) / 0.9f, 0f, 1f);
        }

        return mask;
    }

    /// <summary>1×3×320×320 tensor, RGB, scaled by the image maximum then ImageNet mean/std (U²-Net preprocessing).</summary>
    private static DenseTensor<float> PrepareInput(RasterImage image)
    {
        var opaque = image.FlattenOnto();
        using var bitmap = ImageOps.ToBitmap(opaque);
        using var resized = bitmap.Resize(new SKImageInfo(ModelSize, ModelSize, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                            ?? throw new ConversionException("The picture could not be prepared for the model.");
        var pixels = new byte[ModelSize * ModelSize * 4];
        Marshal.Copy(resized.GetPixels(), pixels, 0, pixels.Length);

        var maxValue = Math.Max((int)pixels.Where((_, i) => i % 4 != 3).Max(), 1);
        float[] mean = [0.485f, 0.456f, 0.406f];
        float[] std = [0.229f, 0.224f, 0.225f];
        var tensor = new DenseTensor<float>([1, 3, ModelSize, ModelSize]);
        for (var y = 0; y < ModelSize; y++)
        {
            for (var x = 0; x < ModelSize; x++)
            {
                var i = (y * ModelSize + x) * 4;
                // BGRA -> R, G, B channels.
                tensor[0, 0, y, x] = (pixels[i + 2] / (float)maxValue - mean[0]) / std[0];
                tensor[0, 1, y, x] = (pixels[i + 1] / (float)maxValue - mean[1]) / std[1];
                tensor[0, 2, y, x] = (pixels[i] / (float)maxValue - mean[2]) / std[2];
            }
        }

        return tensor;
    }

    private static InferenceSession CreateSession()
    {
        using var stream = typeof(BackgroundRemovalEngine).Assembly.GetManifestResourceStream(ModelResource)
                           ?? throw new ConversionException("The background-removal model is missing from this FileOrbit build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new InferenceSession(memory.ToArray(), options);
    }
}
