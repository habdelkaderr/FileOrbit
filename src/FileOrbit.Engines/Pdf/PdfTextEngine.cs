using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileOrbit.Core.Capabilities;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace FileOrbit.Engines.Pdf;

/// <summary>
/// Extracts text and simple tables from PDFs with PdfPig (Apache-2.0). This is extraction, not
/// layout conversion: fonts, images and positioning are not preserved. A layout-preserving
/// engine (Microsoft Word, LibreOffice) is preferred automatically when available.
/// </summary>
public sealed class PdfTextEngine : ConversionEngineBase
{
    public const string EngineId = "pdfpig";
    public const long MaxInputBytes = 512L * 1024 * 1024;

    public override string Id => EngineId;

    public override string DisplayName => "PdfPig text extraction";

    public override string Description => "Extracts text and table data from PDFs. Built in; does not preserve layout.";

    protected override EngineStatus CheckStatus() => EngineStatus.Available("Ready (built in)");

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        yield return Cap("pdf", "txt", Fidelity.Extraction, quality: 70) with
        {
            Note = "Scanned PDFs without a text layer need OCR",
            Options = [LayoutOption],
        };
        yield return Cap("pdf", "html", Fidelity.Extraction, quality: 50) with { Note = "Text only, one section per page" };
        yield return Cap("pdf", "md", Fidelity.Extraction, quality: 50) with { Note = "Text only, one section per page" };
        yield return Cap("pdf", "docx", Fidelity.Extraction, quality: 30) with
        {
            Note = "Text only (no images or layout). Install Microsoft Word or LibreOffice for layout-preserving conversion",
        };
        yield return Cap("pdf", "xlsx", Fidelity.Extraction, quality: 60) with
        {
            Note = "Detects tables from text positions; check the result, complex tables may need tidying",
            Options = [SheetsOption],
        };
        yield return Cap("pdf", "csv", Fidelity.Extraction, quality: 60) with
        {
            Note = "Detects tables from text positions; all pages in one file",
        };
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        if (context.Input.SizeBytes > MaxInputBytes)
        {
            throw new ConversionException("The PDF is too large for text extraction (limit 512 MB).");
        }

        await Task.Run(() => Convert(context, cancellationToken), cancellationToken);
    }

    private static void Convert(ConversionContext context, CancellationToken cancellationToken)
    {
        context.Report(0.02, "Opening PDF");
        using var document = Open(context.Input.Path, context.Password);
        var pages = new List<Page>();
        var totalChars = 0;
        var pageTexts = new List<string>();
        var layout = context.Options.Get("layout", "reading");

        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Report(0.05 + 0.75 * number / document.NumberOfPages, $"Reading page {number} of {document.NumberOfPages}");
            var page = document.GetPage(number);
            pages.Add(page);
            var text = layout == "raw" ? page.Text : ContentOrderTextExtractor.GetText(page, true);
            totalChars += text.Count(c => !char.IsWhiteSpace(c));
            pageTexts.Add(text);
        }

        if (totalChars == 0)
        {
            throw new ConversionException("This PDF has no text layer (it is probably scanned). Use the \"Text (OCR)\" action to recognise the text instead.");
        }

        context.Report(0.85, "Writing output");
        var output = context.PrimaryOutputPath;
        switch (context.Target.Id)
        {
            case "txt":
                File.WriteAllText(output, string.Join("\f" + Environment.NewLine, pageTexts), new UTF8Encoding(true));
                break;
            case "md":
                File.WriteAllText(output, BuildMarkdown(pageTexts), new UTF8Encoding(false));
                break;
            case "html":
                File.WriteAllText(output, BuildHtml(Path.GetFileNameWithoutExtension(context.Input.Path), pageTexts), new UTF8Encoding(false));
                break;
            case "docx":
                WriteDocx(output, pageTexts);
                break;
            case "xlsx":
                WriteXlsx(output, pages, context.Options.Get("sheets", "perPage") == "perPage");
                break;
            case "csv":
                WriteCsv(output, pages);
                break;
            default:
                throw new ConversionException($"PdfPig cannot produce {context.Target.Name}.");
        }

        context.Report(1, "Done");
    }

    internal static PdfDocument Open(string path, string? password)
    {
        try
        {
            var options = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
            if (!string.IsNullOrEmpty(password))
            {
                options.Password = password;
            }

            return PdfDocument.Open(path, options);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new PasswordRequiredException(wasIncorrect: !string.IsNullOrEmpty(password));
        }
        catch (Exception ex) when (ex is not ConversionException and not OperationCanceledException)
        {
            throw new ConversionException("The PDF could not be read. It may be damaged.", ex.Message, ex);
        }
    }

    private static IEnumerable<string> Paragraphs(string pageText) =>
        pageText.Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => string.Join(" ", p.Split('\n', StringSplitOptions.TrimEntries)))
            .Where(p => p.Length > 0);

    private static string BuildMarkdown(IReadOnlyList<string> pages)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine().AppendLine("---").AppendLine();
            }

            foreach (var paragraph in Paragraphs(pages[i]))
            {
                builder.AppendLine(paragraph).AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string BuildHtml(string title, IReadOnlyList<string> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        builder.Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>");
        builder.AppendLine("<style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:48rem;margin:2rem auto;padding:0 1rem;line-height:1.5}section{border-bottom:1px solid #ddd;padding-bottom:1rem}</style>");
        builder.AppendLine("</head><body>");
        for (var i = 0; i < pages.Count; i++)
        {
            builder.Append("<section id=\"page-").Append(i + 1).AppendLine("\">");
            foreach (var paragraph in Paragraphs(pages[i]))
            {
                builder.Append("<p>").Append(WebUtility.HtmlEncode(paragraph)).AppendLine("</p>");
            }

            builder.AppendLine("</section>");
        }

        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static void WriteDocx(string output, IReadOnlyList<string> pages)
    {
        using var document = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        for (var i = 0; i < pages.Count; i++)
        {
            foreach (var paragraph in Paragraphs(pages[i]))
            {
                body.Append(new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(StripInvalidXml(paragraph)) { Space = SpaceProcessingModeValues.Preserve })));
            }

            if (i < pages.Count - 1)
            {
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            }
        }

        body.Append(new SectionProperties());
        main.Document = new Document(body);
        main.Document.Save();
    }

    private static void WriteXlsx(string output, IReadOnlyList<Page> pages, bool sheetPerPage)
    {
        using var workbook = new XLWorkbook();
        IXLWorksheet? single = null;
        var nextRow = 1;
        for (var i = 0; i < pages.Count; i++)
        {
            var rows = TableExtractor.Extract(pages[i]);
            if (rows.Count == 0)
            {
                continue;
            }

            IXLWorksheet sheet;
            if (sheetPerPage)
            {
                sheet = workbook.Worksheets.Add($"Page {i + 1}");
                nextRow = 1;
            }
            else
            {
                sheet = single ??= workbook.Worksheets.Add("Extracted");
            }

            foreach (var row in rows)
            {
                for (var column = 0; column < row.Count; column++)
                {
                    var cell = sheet.Cell(nextRow, column + 1);
                    var value = StripInvalidXml(row[column]);
                    if (TableExtractor.TryParseNumber(value, out var number))
                    {
                        cell.Value = number;
                    }
                    else
                    {
                        cell.Value = value;
                    }
                }

                nextRow++;
            }

            nextRow++;
            sheet.Columns().AdjustToContents(1, 200);
        }

        if (workbook.Worksheets.Count == 0)
        {
            workbook.Worksheets.Add("Extracted");
        }

        workbook.SaveAs(output);
    }

    private static void WriteCsv(string output, IReadOnlyList<Page> pages)
    {
        using var writer = new StreamWriter(output, false, new UTF8Encoding(true));
        foreach (var page in pages)
        {
            foreach (var row in TableExtractor.Extract(page))
            {
                writer.WriteLine(string.Join(",", row.Select(Data.CsvFormat.Escape)));
            }
        }
    }

    private static string StripInvalidXml(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (XmlConvertIsValid(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();

        static bool XmlConvertIsValid(char c) => c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF);
    }

    private static readonly OptionDescriptor LayoutOption = OptionDescriptor.Choice("layout", "Text order", "reading",
        new("reading", "Reading order", "Follows columns and paragraphs"),
        new("raw", "As stored", "The order text appears in the file"));

    private static readonly OptionDescriptor SheetsOption = OptionDescriptor.Choice("sheets", "Worksheets", "perPage",
        new("perPage", "One sheet per page"),
        new("single", "Everything on one sheet"));
}

/// <summary>Heuristic table detection from word positions on a PDF page.</summary>
internal static class TableExtractor
{
    public static List<List<string>> Extract(Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var heights = words.Select(w => w.BoundingBox.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        var medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 10;
        var lineTolerance = Math.Max(1.5, medianHeight * 0.5);

        // Group words into lines by baseline.
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= lineTolerance);
            if (line is null)
            {
                lines.Add([word]);
            }
            else
            {
                line.Add(word);
            }
        }

        // Split each line into cells at large horizontal gaps.
        var gapThreshold = Math.Max(4, medianHeight * 1.2);
        var cellLines = new List<List<(double Left, string Text)>>();
        foreach (var line in lines)
        {
            var sorted = line.OrderBy(w => w.BoundingBox.Left).ToList();
            var cells = new List<(double Left, string Text)>();
            var currentText = new StringBuilder(sorted[0].Text);
            var currentLeft = sorted[0].BoundingBox.Left;
            var previousRight = sorted[0].BoundingBox.Right;
            foreach (var word in sorted.Skip(1))
            {
                if (word.BoundingBox.Left - previousRight > gapThreshold)
                {
                    cells.Add((currentLeft, currentText.ToString()));
                    currentText.Clear().Append(word.Text);
                    currentLeft = word.BoundingBox.Left;
                }
                else
                {
                    currentText.Append(' ').Append(word.Text);
                }

                previousRight = word.BoundingBox.Right;
            }

            cells.Add((currentLeft, currentText.ToString()));
            cellLines.Add(cells);
        }

        // Cluster cell start positions into columns shared by the whole page.
        var columnTolerance = Math.Max(6, medianHeight);
        var columns = new List<double>();
        foreach (var left in cellLines.SelectMany(c => c).Select(c => c.Left).OrderBy(x => x))
        {
            if (columns.Count == 0 || left - columns[^1] > columnTolerance)
            {
                columns.Add(left);
            }
        }

        var rows = new List<List<string>>();
        foreach (var cells in cellLines)
        {
            var row = Enumerable.Repeat(string.Empty, columns.Count).ToList();
            foreach (var (left, text) in cells)
            {
                var index = NearestColumn(columns, left);
                row[index] = row[index].Length == 0 ? text : row[index] + " " + text;
            }

            // Trim trailing empty cells.
            while (row.Count > 0 && row[^1].Length == 0)
            {
                row.RemoveAt(row.Count - 1);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static bool TryParseNumber(string text, out double value)
    {
        value = 0;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 30)
        {
            return false;
        }

        var negative = trimmed.StartsWith('(') && trimmed.EndsWith(')');
        if (negative)
        {
            trimmed = trimmed[1..^1];
        }

        // Leading zeros usually mean an identifier (postcode, account number), not a number.
        if (trimmed.Length > 1 && trimmed[0] == '0' && char.IsDigit(trimmed[1]))
        {
            return false;
        }

        var styles = NumberStyles.Number | NumberStyles.AllowCurrencySymbol;
        if (double.TryParse(trimmed, styles, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(trimmed, styles, CultureInfo.CurrentCulture, out value))
        {
            value = negative ? -value : value;
            return true;
        }

        return false;
    }

    private static int NearestColumn(List<double> columns, double left)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < columns.Count; i++)
        {
            var distance = Math.Abs(columns[i] - left);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }
}
