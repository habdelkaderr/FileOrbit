using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileOrbit.Engines.Images;
using SkiaSharp;

namespace FileOrbit.Tests.Support;

/// <summary>
/// Generates small, legally unencumbered sample files on the fly, so no binary test fixtures need
/// to be committed or redistributed.
/// </summary>
public static class SampleFiles
{
    public static RasterImage Gradient(int width = 64, int height = 48, bool transparent = true)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var alpha = transparent && x < width / 4 ? (byte)128 : (byte)255;
                // Premultiplied BGRA.
                pixels[i] = (byte)(y * 255 / height * alpha / 255);
                pixels[i + 1] = (byte)(x * 255 / width * alpha / 255);
                pixels[i + 2] = (byte)(128 * alpha / 255);
                pixels[i + 3] = alpha;
            }
        }

        return new RasterImage(pixels, width, height);
    }

    public static async Task<string> PngAsync(TestDirectory dir, string name = "sample.png", int width = 64, int height = 48)
    {
        var path = dir.File(name);
        await WindowsImaging.EncodeAsync(Gradient(width, height), "png", path, 1, MetadataMode.Remove, CancellationToken.None);
        return path;
    }

    public static async Task<string> JpegAsync(TestDirectory dir, string name = "photo.jpg", int width = 640, int height = 480)
    {
        var path = dir.File(name);
        await WindowsImaging.EncodeAsync(Gradient(width, height, transparent: false), "jpeg", path, 0.9, MetadataMode.Remove, CancellationToken.None);
        return path;
    }

    public static string Svg(TestDirectory dir, string name = "logo.svg") => dir.Write(name,
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
          <rect width="120" height="80" rx="12" fill="#3b82f6"/>
          <circle cx="60" cy="40" r="24" fill="#fff"/>
          <image href="https://example.invalid/tracker.png" width="1" height="1"/>
        </svg>
        """);

    /// <summary>A PDF with real text on each page, created with Skia's PDF backend.</summary>
    public static string Pdf(TestDirectory dir, string name = "report.pdf", int pages = 2)
    {
        var path = dir.File(name);
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream)!;
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = new SKFont(typeface, 14);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        for (var page = 1; page <= pages; page++)
        {
            var canvas = document.BeginPage(595, 842);
            canvas.DrawText($"Quarterly Report page {page}", 72, 72, SKTextAlign.Left, font, paint);
            canvas.DrawText("Region", 72, 120, SKTextAlign.Left, font, paint);
            canvas.DrawText("Revenue", 300, 120, SKTextAlign.Left, font, paint);
            canvas.DrawText("North", 72, 140, SKTextAlign.Left, font, paint);
            canvas.DrawText("1200", 300, 140, SKTextAlign.Left, font, paint);
            canvas.DrawText("South", 72, 160, SKTextAlign.Left, font, paint);
            canvas.DrawText("950", 300, 160, SKTextAlign.Left, font, paint);
            document.EndPage();
        }

        document.Close();
        return path;
    }

    /// <summary>A picture of text (no text layer), for OCR tests.</summary>
    public static string TextImage(TestDirectory dir, string text, string name = "scan.png")
    {
        var path = dir.File(name);
        using var bitmap = new SKBitmap(900, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var typeface = SKTypeface.FromFamilyName("Arial");
            using var font = new SKFont(typeface, 56);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText(text, 40, 120, SKTextAlign.Left, font, paint);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public static string Csv(TestDirectory dir, string name = "people.csv") => dir.Write(name,
        "Name,Age,Zip,Joined,Note\r\n" +
        "Ada,36,02139,2024-01-15,\"Likes \"\"maths\"\", engines\"\r\n" +
        "Grace,45,10001,2023-11-02,\"Line one\nLine two\"\r\n" +
        "Linus,28,94016,2022-06-30,=1+2\r\n");

    public static string Tsv(TestDirectory dir, string name = "data.tsv") => dir.Write(name, "a\tb\tc\n1\t2\t3\n4\t5\t6\n");

    public static string Json(TestDirectory dir, string name = "items.json") => dir.Write(name,
        """[{"id":1,"name":"Widget","tags":["a","b"],"price":9.5},{"id":2,"name":"Gadget","inStock":true}]""");

    public static string Xlsx(TestDirectory dir, string name = "budget.xlsx")
    {
        var path = dir.File(name);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Budget");
        sheet.Cell(1, 1).Value = "Item";
        sheet.Cell(1, 2).Value = "Cost";
        sheet.Cell(2, 1).Value = "Rent";
        sheet.Cell(2, 2).Value = 1200;
        sheet.Cell(3, 1).Value = "Food";
        sheet.Cell(3, 2).Value = 350.5;
        sheet.Cell(4, 1).Value = "Total";
        sheet.Cell(4, 2).FormulaA1 = "SUM(B2:B3)";
        var second = workbook.Worksheets.Add("Notes");
        second.Cell(1, 1).Value = "Second sheet";
        workbook.SaveAs(path);
        return path;
    }

    public static string Docx(TestDirectory dir, string name = "letter.docx")
    {
        var path = dir.File(name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body(
            new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text("Project Orbit"))),
            new Paragraph(new Run(new RunProperties(new Bold()), new Text("Bold intro")), new Run(new Text(" and plain text.") { Space = SpaceProcessingModeValues.Preserve })),
            new Table(
                new TableRow(new TableCell(new Paragraph(new Run(new Text("Key")))), new TableCell(new Paragraph(new Run(new Text("Value"))))),
                new TableRow(new TableCell(new Paragraph(new Run(new Text("Speed")))), new TableCell(new Paragraph(new Run(new Text("Fast")))))),
            new Paragraph(new Run(new Text("The end.")))));
        main.Document.Save();
        return path;
    }

    public static string Markdown(TestDirectory dir, string name = "readme.md") => dir.Write(name,
        "# Title\n\nSome **bold** text and a [link](https://example.com).\n\n<script>alert(1)</script>\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");

    public static string Html(TestDirectory dir, string name = "page.html") => dir.Write(name,
        "<!DOCTYPE html><html><head><title>T</title><style>p{color:red}</style><script>var x=1;</script></head><body><h1>Hello</h1><p>First &amp; second</p><ul><li>One</li><li>Two</li></ul></body></html>");

    /// <summary>A short mono sine wave as 16-bit PCM WAV.</summary>
    public static string Wav(TestDirectory dir, string name = "tone.wav", double seconds = 1.0, int sampleRate = 44100)
    {
        var samples = (int)(seconds * sampleRate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * short.MaxValue * 0.3));
        }

        writer.Flush();
        return dir.Write(name, stream.ToArray());
    }

    public static string Zip(TestDirectory dir, string name = "bundle.zip", params (string Entry, string Content)[] entries)
    {
        var path = dir.File(name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries.Length > 0 ? entries : [("docs/readme.txt", "hello"), ("data/values.csv", "a,b\n1,2\n")])
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }

    public static string TarGz(TestDirectory dir, string name = "bundle.tar.gz", bool includeSymlink = false)
    {
        var path = dir.File(name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "project/notes.txt") { DataStream = new MemoryStream("tar content"u8.ToArray()) };
        writer.WriteEntry(entry);
        if (includeSymlink)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "project/link") { LinkName = "C:\\Windows\\System32" });
        }

        return path;
    }

    public static string Gz(TestDirectory dir, string name = "log.txt.gz", string content = "compressed log line\n")
    {
        var path = dir.File(name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(Encoding.UTF8.GetBytes(content));
        return path;
    }
}
