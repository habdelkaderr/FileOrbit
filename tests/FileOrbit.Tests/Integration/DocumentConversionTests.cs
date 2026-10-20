using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using FileOrbit.Engines.Pdf;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Integration;

public sealed class DocumentConversionTests
{
    private static readonly Pipeline P = Pipeline.Real;

    [Fact]
    public async Task Pdf_pages_render_to_png_folder()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir, pages: 3);

        var job = await P.ConvertAsync(pdf, "convert:png", new Dictionary<string, string> { ["dpi"] = "72" });

        var folder = job.PrimaryOutput!;
        Assert.True(Directory.Exists(folder), "Multi-page output should be a folder");
        var pages = Directory.GetFiles(folder, "*.png").Order().ToArray();
        Assert.Equal(3, pages.Length);
        Assert.All(pages, page => P.AssertFormat(page, "png"));
        Assert.EndsWith("report - page 01.png", pages[0]);
    }

    [Fact]
    public async Task Single_page_pdf_renders_to_single_jpeg()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir, pages: 1);

        var job = await P.ConvertAsync(pdf, "convert:jpeg");

        P.AssertFormat(job.PrimaryOutput!, "jpeg");
        Assert.EndsWith("report.jpg", job.PrimaryOutput);
    }

    [Fact]
    public async Task Pdf_to_multipage_tiff()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir, pages: 2);

        var job = await P.ConvertAsync(pdf, "convert:tiff", new Dictionary<string, string> { ["dpi"] = "72" });

        P.AssertFormat(job.PrimaryOutput!, "tiff");
    }

    [Fact]
    public async Task Pdf_text_extraction_contains_page_text()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir);

        var job = await P.ConvertAsync(pdf, "convert:txt", engineId: PdfTextEngine.EngineId);

        var text = await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken);
        Assert.Contains("Quarterly Report page 1", text);
        Assert.Contains("Quarterly Report page 2", text);
    }

    [Fact]
    public async Task Pdf_to_docx_extraction_creates_valid_word_document()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir);

        var job = await P.ConvertAsync(pdf, "convert:docx", engineId: PdfTextEngine.EngineId);

        P.AssertFormat(job.PrimaryOutput!, "docx");
        using var document = WordprocessingDocument.Open(job.PrimaryOutput!, false);
        Assert.Contains("Quarterly Report", document.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public async Task Pdf_to_xlsx_detects_table_columns_and_numbers()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir, pages: 1);

        var job = await P.ConvertAsync(pdf, "convert:xlsx");

        using var workbook = new XLWorkbook(job.PrimaryOutput!);
        var sheet = workbook.Worksheet(1);
        var north = sheet.CellsUsed().First(c => c.GetString() == "North");
        var revenue = sheet.Cell(north.Address.RowNumber, north.Address.ColumnNumber + 1);
        Assert.Equal(1200, revenue.GetDouble());
    }

    [Fact]
    public async Task Pdf_to_html_and_markdown()
    {
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir);

        var html = await P.ConvertAsync(pdf, "convert:html", engineId: PdfTextEngine.EngineId);
        var md = await P.ConvertAsync(pdf, "convert:md");

        Assert.Contains("<section id=\"page-2\">", await File.ReadAllTextAsync(html.PrimaryOutput!, TestContext.Current.CancellationToken));
        Assert.Contains("Quarterly Report", await File.ReadAllTextAsync(md.PrimaryOutput!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Docx_exports_text_markdown_and_html_without_office()
    {
        using var dir = new TestDirectory();
        var docx = SampleFiles.Docx(dir);

        var txt = await P.ConvertAsync(docx, "convert:txt", engineId: "openxml-docx");
        var md = await P.ConvertAsync(docx, "convert:md");
        var html = await P.ConvertAsync(docx, "convert:html", engineId: "openxml-docx");

        Assert.Contains("Bold intro and plain text.", await File.ReadAllTextAsync(txt.PrimaryOutput!, TestContext.Current.CancellationToken));
        var markdown = await File.ReadAllTextAsync(md.PrimaryOutput!, TestContext.Current.CancellationToken);
        Assert.Contains("# Project Orbit", markdown);
        Assert.Contains("**Bold intro**", markdown);
        Assert.Contains("| Speed | Fast |", markdown);
        Assert.Contains("<h1>Project Orbit</h1>", await File.ReadAllTextAsync(html.PrimaryOutput!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Markdown_to_html_does_not_pass_raw_script()
    {
        using var dir = new TestDirectory();
        var md = SampleFiles.Markdown(dir);

        var job = await P.ConvertAsync(md, "convert:html");

        var html = await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<table>", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public async Task Html_to_text_drops_scripts_and_styles()
    {
        using var dir = new TestDirectory();
        var html = SampleFiles.Html(dir);

        var job = await P.ConvertAsync(html, "convert:txt");

        var text = await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken);
        Assert.Contains("Hello", text);
        Assert.Contains("First & second", text);
        Assert.Contains("• One", text);
        Assert.DoesNotContain("var x", text);
        Assert.DoesNotContain("color:red", text);
    }

    [Fact]
    public async Task Ocr_recognises_text_in_image()
    {
        using var dir = new TestDirectory();
        var image = SampleFiles.TextImage(dir, "FileOrbit converts files");

        var job = await P.ConvertAsync(image, "convert:txt:ocr");

        var text = await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken);
        Assert.Contains("FileOrbit", text, StringComparison.OrdinalIgnoreCase);
    }
}
