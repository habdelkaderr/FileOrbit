using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FileOrbit.Engines.Ocr;

/// <summary>
/// Optical character recognition with the OCR engine built into Windows (Windows.Media.Ocr).
/// Recognises text in images and scanned PDFs entirely on this PC.
/// </summary>
public sealed class WindowsOcrEngine : ConversionEngineBase
{
    public const string EngineId = "windows-ocr";
    private static readonly string[] ImageSources = ["png", "jpeg", "bmp", "tiff", "gif", "webp", "heic"];

    public override string Id => EngineId;

    public override string DisplayName => "Windows OCR";

    public override string Description => "Recognises text in images and scanned PDFs using the OCR engine built into Windows.";

    protected override EngineStatus CheckStatus()
    {
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages;
            if (languages.Count == 0)
            {
                return EngineStatus.Unavailable("No OCR languages are installed. Add a language with OCR support in Windows Settings › Time & language › Language & region.");
            }

            return EngineStatus.Available($"Ready: {string.Join(", ", languages.Select(l => l.DisplayName))}");
        }
        catch (Exception ex)
        {
            return EngineStatus.Unavailable($"Windows OCR is not available: {ex.Message}");
        }
    }

    public override CapabilityStatus GetCapabilityStatus(ConversionCapability capability)
    {
        var status = GetStatus().ToCapabilityStatus();
        if (status.IsAvailable && capability.SourceFormatId is "webp" or "heic" && !Images.WindowsImaging.CanDecode("." + capability.SourceFormatId))
        {
            return CapabilityStatus.Missing($"Reading {capability.SourceFormatId.ToUpperInvariant()} needs the matching image extension from the Microsoft Store.");
        }

        return status;
    }

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var source in ImageSources.Append("pdf"))
        {
            yield return Cap(source, "txt", Fidelity.Extraction, quality: 50) with
            {
                Variant = "ocr",
                Label = "Text (OCR)",
                Note = source == "pdf" ? "Recognises text on each page; use for scanned PDFs" : "Recognises printed text in the picture",
                Options = [LanguageOption()],
                Cost = source == "pdf" ? ResourceCost.High : ResourceCost.Medium,
            };
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var engine = CreateEngine(context.Options.Get("language", "auto"));
        var text = new StringBuilder();
        if (context.Input.Format.Id == "pdf")
        {
            await using var file = new FileStream(context.Input.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            using var stream = file.AsRandomAccessStream();
            var document = await WindowsPdfEngine.LoadAsync(stream, context.Password, cancellationToken);
            for (uint index = 0; index < document.PageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Report((double)index / document.PageCount, $"Recognising page {index + 1} of {document.PageCount}");
                using var page = document.GetPage(index);
                using var memory = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(memory, WindowsPdfEngine.RenderOptions(page, 300, BitmapEncoder.PngEncoderId)).AsTask(cancellationToken);
                var decoder = await BitmapDecoder.CreateAsync(memory).AsTask(cancellationToken);
                if (index > 0)
                {
                    text.AppendLine().AppendLine("\f");
                }

                text.Append(await RecognizeAsync(engine, decoder, cancellationToken));
            }
        }
        else
        {
            await using var file = new FileStream(context.Input.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            context.Report(0.3, "Recognising text");
            text.Append(await RecognizeAsync(engine, decoder, cancellationToken));
        }

        if (text.ToString().Trim().Length == 0)
        {
            context.AddWarning("No text was recognised.");
        }

        await File.WriteAllTextAsync(context.PrimaryOutputPath, text.ToString(), new UTF8Encoding(true), cancellationToken);
        context.Report(1, "Done");
    }

    private static async Task<string> RecognizeAsync(OcrEngine engine, BitmapDecoder decoder, CancellationToken cancellationToken)
    {
        // OCR has a maximum image dimension; scale down larger images.
        var max = OcrEngine.MaxImageDimension;
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        var width = decoder.OrientedPixelWidth;
        var height = decoder.OrientedPixelHeight;
        if (width > max || height > max)
        {
            var scale = (double)max / Math.Max(width, height);
            var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
            var scaledWidth = (uint)(width * scale);
            var scaledHeight = (uint)(height * scale);
            transform.ScaledWidth = rotated ? scaledHeight : scaledWidth;
            transform.ScaledHeight = rotated ? scaledWidth : scaledHeight;
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }

    private static OcrEngine CreateEngine(string language)
    {
        OcrEngine? engine = null;
        if (language != "auto" && Language.IsWellFormed(language))
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language(language));
        }

        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        return engine ?? throw new ConversionException("No OCR language is available. Install a language with OCR support in Windows Settings.");
    }

    private static OptionDescriptor LanguageOption()
    {
        var choices = new List<OptionChoice> { new("auto", "Windows display language") };
        try
        {
            choices.AddRange(OcrEngine.AvailableRecognizerLanguages.Select(l => new OptionChoice(l.LanguageTag, l.DisplayName)));
        }
        catch (Exception)
        {
            // OCR not available; the engine status explains why.
        }

        return OptionDescriptor.Choice("language", "Language", "auto", [.. choices]);
    }
}
