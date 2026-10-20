using System.Runtime.InteropServices.WindowsRuntime;
using FileOrbit.Core.Capabilities;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FileOrbit.Engines.Pdf;

/// <summary>
/// Renders PDF pages to images with the PDF renderer built into Windows (Windows.Data.Pdf).
/// No external software is needed.
/// </summary>
public sealed class WindowsPdfEngine : ConversionEngineBase
{
    public const string EngineId = "windows-pdf";
    private const long MaxPixelsPerPage = 120_000_000;
    private const int WrongPasswordHResult = unchecked((int)0x8007052B);

    public override string Id => EngineId;

    public override string DisplayName => "Windows PDF renderer";

    public override string Description => "Renders PDF pages to images using the PDF engine built into Windows.";

    protected override EngineStatus CheckStatus() => EngineStatus.Available("Ready (built into Windows)");

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var target in new[] { "png", "jpeg", "tiff" })
        {
            yield return Cap("pdf", target, target == "jpeg" ? Fidelity.Lossy : Fidelity.Rasterized, quality: 80) with
            {
                Shape = target == "tiff" ? OutputShape.File : OutputShape.FileOrFolder,
                Note = target == "tiff" ? "All pages in one multi-page TIFF" : "One image per page; several pages are saved together in a folder",
                Cost = ResourceCost.Medium,
                Options = [ResolutionOption, PagesOption],
            };
        }

        yield return Cap("pdf", "pptx", Fidelity.Rasterized, quality: 60) with
        {
            Note = "Each page becomes a picture slide (not editable). For editable text convert to DOCX instead",
            Cost = ResourceCost.Medium,
            Options = [ResolutionOption],
        };
    }

    public override Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken) =>
        RenderPdfAsync(context.Input.Path, context.Password, context, cancellationToken);

    /// <summary>
    /// Renders a PDF into the context's staging folder as <see cref="ConversionContext.Target"/> images.
    /// Also used by other engines that first export to PDF (e.g. Word documents to PNG).
    /// </summary>
    internal static async Task RenderPdfAsync(string pdfPath, string? password, ConversionContext context, CancellationToken cancellationToken)
    {
        context.Report(0.02, "Opening PDF");
        await using var file = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        using var stream = file.AsRandomAccessStream();
        var document = await LoadAsync(stream, password, cancellationToken);

        var pageCount = (int)document.PageCount;
        if (pageCount == 0)
        {
            throw new ConversionException("The PDF has no pages.");
        }

        var pages = context.Options.Get("pages", "all") == "first" ? 1 : pageCount;
        var dpi = context.Options.GetInt("dpi", 150);
        var target = context.Target.Id;
        var digits = Math.Max(2, pages.ToString().Length);

        if (target == "tiff")
        {
            await RenderMultiPageTiffAsync(context, document, pages, dpi, cancellationToken);
            return;
        }

        if (target == "pptx")
        {
            await RenderSlidesAsync(context, document, pageCount, dpi, cancellationToken);
            return;
        }

        var encoderId = target == "jpeg" ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
        for (uint index = 0; index < pages; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Report((double)index / pages, $"Rendering page {index + 1} of {pages}");
            var name = pages == 1
                ? context.OutputBaseName + context.Target.PrimaryExtension
                : $"{context.OutputBaseName} - page {(index + 1).ToString().PadLeft(digits, '0')}{context.Target.PrimaryExtension}";
            await using var output = new FileStream(context.OutputPath(name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true);
            using var outputStream = output.AsRandomAccessStream();
            using var page = document.GetPage(index);
            await page.RenderToStreamAsync(outputStream, RenderOptions(page, dpi, encoderId)).AsTask(cancellationToken);
        }

        if (pages < pageCount)
        {
            context.AddWarning($"Only the first of {pageCount} pages was converted.");
        }

        context.Report(1, "Done");
    }

    internal static async Task<PdfDocument> LoadAsync(IRandomAccessStream stream, string? password, CancellationToken cancellationToken)
    {
        try
        {
            return string.IsNullOrEmpty(password)
                ? await PdfDocument.LoadFromStreamAsync(stream).AsTask(cancellationToken)
                : await PdfDocument.LoadFromStreamAsync(stream, password).AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex.HResult == WrongPasswordHResult)
        {
            throw new PasswordRequiredException(wasIncorrect: !string.IsNullOrEmpty(password));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ConversionException)
        {
            throw new ConversionException("Windows could not open this PDF. It may be damaged.", ex.Message, ex);
        }
    }

    internal static PdfPageRenderOptions RenderOptions(PdfPage page, int dpi, Guid encoderId)
    {
        var scale = dpi / 96.0; // PdfPage.Size is in DIPs (1/96 inch).
        var width = Math.Max(1, page.Size.Width * scale);
        var height = Math.Max(1, page.Size.Height * scale);
        if (width * height > MaxPixelsPerPage)
        {
            var shrink = Math.Sqrt(MaxPixelsPerPage / (width * height));
            width *= shrink;
            height *= shrink;
        }

        return new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Round(width),
            DestinationHeight = (uint)Math.Round(height),
            BitmapEncoderId = encoderId,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };
    }

    private static async Task RenderSlidesAsync(ConversionContext context, PdfDocument document, int pages, int dpi, CancellationToken cancellationToken)
    {
        var slides = new List<SlideDeckWriter.Slide>();
        for (uint index = 0; index < pages; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Report(0.9 * index / pages, $"Rendering page {index + 1} of {pages}");
            using var page = document.GetPage(index);
            using var memory = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(memory, RenderOptions(page, dpi, BitmapEncoder.PngEncoderId)).AsTask(cancellationToken);
            var bytes = new byte[memory.Size];
            memory.Seek(0);
            await memory.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken);
            // PdfPage.Size is in DIPs (1/96 inch); slides are measured in points (1/72 inch).
            slides.Add(new SlideDeckWriter.Slide(bytes, page.Size.Width * 72 / 96, page.Size.Height * 72 / 96));
        }

        context.Report(0.95, "Writing presentation");
        await Task.Run(() => SlideDeckWriter.Write(context.PrimaryOutputPath, slides), cancellationToken);
        context.Report(1, "Done");
    }

    private static async Task RenderMultiPageTiffAsync(ConversionContext context, PdfDocument document, int pages, int dpi, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(context.PrimaryOutputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true);
        using var outputStream = output.AsRandomAccessStream();
        var options = new BitmapPropertySet { { "TiffCompressionMethod", new BitmapTypedValue((byte)4, Windows.Foundation.PropertyType.UInt8) } };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, outputStream, options).AsTask(cancellationToken);

        for (uint index = 0; index < pages; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Report((double)index / pages, $"Rendering page {index + 1} of {pages}");
            using var page = document.GetPage(index);
            using var memory = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(memory, RenderOptions(page, dpi, BitmapEncoder.PngEncoderId)).AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(memory).AsTask(cancellationToken);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
            if (index > 0)
            {
                await encoder.GoToNextFrameAsync().AsTask(cancellationToken);
            }

            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, decoder.PixelWidth, decoder.PixelHeight, dpi, dpi, pixels.DetachPixelData());
        }

        await encoder.FlushAsync().AsTask(cancellationToken);
        context.Report(1, "Done");
    }

    internal static readonly OptionDescriptor ResolutionOption = OptionDescriptor.Choice("dpi", "Resolution", "150",
        new("72", "72 DPI", "Screen preview, smallest files"),
        new("150", "150 DPI", "Good for screens and sharing"),
        new("200", "200 DPI"),
        new("300", "300 DPI", "Print quality, large files"));

    internal static readonly OptionDescriptor PagesOption = OptionDescriptor.Choice("pages", "Pages", "all",
        new("all", "All pages"),
        new("first", "First page only"));
}
