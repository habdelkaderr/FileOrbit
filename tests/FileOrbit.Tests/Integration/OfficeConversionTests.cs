using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using FileOrbit.Engines.Office;
using FileOrbit.Engines.Pdf;
using FileOrbit.Tests.Support;
using UglyToad.PdfPig;

namespace FileOrbit.Tests.Integration;

/// <summary>
/// Real conversions through Microsoft Office automation. Skipped automatically when Word, Excel or
/// PowerPoint is not installed. Office runs invisibly; these tests take a while.
/// </summary>
[Trait("Category", "Office")]
public sealed class OfficeConversionTests
{
    private static readonly Pipeline P = Pipeline.Real;

    private static string PdfText(string path)
    {
        using var document = PdfDocument.Open(path);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }

    [Fact]
    public async Task Word_converts_docx_to_pdf_with_text()
    {
        using var dir = new TestDirectory();
        var docx = SampleFiles.Docx(dir);

        var job = await P.ConvertAsync(docx, "convert:pdf", engineId: OfficeEngine.EngineId);

        P.AssertFormat(job.PrimaryOutput!, "pdf");
        Assert.Contains("Project Orbit", PdfText(job.PrimaryOutput!));
    }

    [Fact]
    public async Task Word_converts_docx_to_pdfa()
    {
        using var dir = new TestDirectory();
        var docx = SampleFiles.Docx(dir);

        var job = await P.ConvertAsync(docx, "convert:pdf", new Dictionary<string, string> { ["pdfa"] = "true" }, engineId: OfficeEngine.EngineId);

        var bytes = await File.ReadAllBytesAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken);
        Assert.Contains("pdfaid", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public async Task Word_reflows_pdf_into_editable_docx()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir);

        var job = await P.ConvertAsync(pdf, "convert:docx", engineId: OfficeEngine.EngineId);

        P.AssertFormat(job.PrimaryOutput!, "docx");
        using var document = WordprocessingDocument.Open(job.PrimaryOutput!, false);
        Assert.Contains("Quarterly Report", document.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public async Task Word_renders_docx_pages_to_png()
    {
        using var dir = new TestDirectory();
        var docx = SampleFiles.Docx(dir);

        var job = await P.ConvertAsync(docx, "convert:png", engineId: OfficeEngine.EngineId);

        P.AssertFormat(job.PrimaryOutput!, "png");
        Assert.Single(Directory.GetFileSystemEntries(dir.Path, "letter*.png"));
        P.AssertNoStagingLeft(dir.Path);
    }

    [Fact]
    public async Task Word_exports_single_file_html_with_text()
    {
        using var dir = new TestDirectory();
        var docx = SampleFiles.Docx(dir);

        var job = await P.ConvertAsync(docx, "convert:html", engineId: OfficeEngine.EngineId);

        Assert.True(File.Exists(job.PrimaryOutput));
        Assert.Contains("Project Orbit", await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(dir.Path)); // Assets are inlined; no "_files" folder left behind.
    }

    [Fact]
    public async Task Word_converts_markdown_to_docx_without_remote_content()
    {
        using var dir = new TestDirectory();
        var md = dir.Write("notes.md", "# Heading\n\nText with ![remote](https://example.invalid/pixel.png) image.\n");

        var job = await P.ConvertAsync(md, "convert:docx", engineId: OfficeEngine.EngineId);

        P.AssertFormat(job.PrimaryOutput!, "docx");
        Assert.Contains(job.Warnings, w => w.Contains("Remote"));
    }

    [Fact]
    public async Task Excel_converts_xlsx_to_pdf_and_ods()
    {
        using var dir = new TestDirectory();
        var xlsx = SampleFiles.Xlsx(dir);

        var pdf = await P.ConvertAsync(xlsx, "convert:pdf", engineId: OfficeEngine.EngineId);
        var ods = await P.ConvertAsync(xlsx, "convert:ods", engineId: OfficeEngine.EngineId);

        Assert.Contains("Rent", PdfText(pdf.PrimaryOutput!));
        P.AssertFormat(ods.PrimaryOutput!, "ods");
    }

    [Fact]
    public async Task Excel_converts_legacy_xls_back_to_xlsx()
    {
        using var dir = new TestDirectory();
        var xlsx = SampleFiles.Xlsx(dir);
        var xls = await P.ConvertAsync(xlsx, "convert:xls", engineId: OfficeEngine.EngineId);
        P.AssertFormat(xls.PrimaryOutput!, "xls");

        var back = await P.ConvertAsync(xls.PrimaryOutput!, "convert:xlsx", engineId: OfficeEngine.EngineId);

        using var workbook = new XLWorkbook(back.PrimaryOutput!);
        Assert.Equal(1550.5, workbook.Worksheet(1).Cell(4, 2).GetDouble());
    }

    [Fact]
    public async Task Pdf_to_pptx_picture_slides_open_in_powerpoint_and_convert_back()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir, pages: 3);

        // FileOrbit's own PPTX writer (no Office needed) ...
        var pptx = await P.ConvertAsync(pdf, "convert:pptx", engineId: WindowsPdfEngine.EngineId);
        P.AssertFormat(pptx.PrimaryOutput!, "pptx");
        using (var presentation = PresentationDocument.Open(pptx.PrimaryOutput!, false))
        {
            Assert.Equal(3, presentation.PresentationPart!.SlideParts.Count());
        }

        // ... must be accepted by real PowerPoint.
        var slides = await P.ConvertAsync(pptx.PrimaryOutput!, "convert:png", engineId: OfficeEngine.EngineId);
        Assert.True(Directory.Exists(slides.PrimaryOutput));
        Assert.Equal(3, Directory.GetFiles(slides.PrimaryOutput!, "*.png").Length);

        var backToPdf = await P.ConvertAsync(pptx.PrimaryOutput!, "convert:pdf", engineId: OfficeEngine.EngineId);
        using var document = PdfDocument.Open(backToPdf.PrimaryOutput!);
        Assert.Equal(3, document.NumberOfPages);
    }
}
