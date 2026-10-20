using System.Net;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileOrbit.Core.Capabilities;

namespace FileOrbit.Engines.Text;

/// <summary>
/// Reads DOCX text directly with the Open XML SDK (MIT), so text, HTML and Markdown export works
/// without Microsoft Word. Formatting is simplified: headings, bold/italic, lists and tables are
/// kept; images, columns and exact layout are not.
/// </summary>
public sealed class DocxTextEngine : ConversionEngineBase
{
    public const string EngineId = "openxml-docx";

    public override string Id => EngineId;

    public override string DisplayName => "Open XML document reader";

    public override string Description => "Extracts text, headings, lists and tables from Word documents. Built in.";

    protected override EngineStatus CheckStatus() => EngineStatus.Available("Ready (built in)");

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        yield return Cap("docx", "txt", Fidelity.Extraction, quality: 70);
        yield return Cap("docx", "md", Fidelity.Extraction, quality: 70) with { Note = "Keeps headings, emphasis, lists and tables; images are not included" };
        yield return Cap("docx", "html", Fidelity.Extraction, quality: 40) with { Note = "Simplified HTML; images are not included. Microsoft Word gives a closer match" };
    }

    public override Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            context.Report(0.1, "Reading document");
            WordprocessingDocument document;
            try
            {
                document = WordprocessingDocument.Open(context.Input.Path, false, new OpenSettings { AutoSave = false });
            }
            catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException)
            {
                throw new ConversionException("The document could not be read. It may be damaged or password protected.", ex.Message, ex);
            }

            using (document)
            {
                var body = document.MainDocumentPart?.Document?.Body ?? throw new ConversionException("The document has no content.");
                var blocks = ReadBlocks(body, cancellationToken).ToList();
                if (document.MainDocumentPart!.ImageParts.Any())
                {
                    context.AddWarning("Images in the document were not included.");
                }

                var title = Path.GetFileNameWithoutExtension(context.Input.Path);
                var output = context.Target.Id switch
                {
                    "txt" => RenderText(blocks),
                    "md" => RenderMarkdown(blocks),
                    "html" => HtmlTools.WrapDocument(RenderHtml(blocks), title),
                    _ => throw new ConversionException($"Cannot produce {context.Target.Name}."),
                };
                File.WriteAllText(context.PrimaryOutputPath, output, new UTF8Encoding(context.Target.Id == "txt"));
            }

            context.Report(1, "Done");
        }, cancellationToken);

    private abstract record Block;

    private sealed record ParagraphBlock(int HeadingLevel, bool IsListItem, IReadOnlyList<Span> Spans) : Block
    {
        public string PlainText => string.Concat(Spans.Select(s => s.Text));
    }

    private sealed record TableBlock(IReadOnlyList<IReadOnlyList<string>> Rows) : Block;

    private sealed record Span(string Text, bool Bold, bool Italic);

    private static IEnumerable<Block> ReadBlocks(Body body, CancellationToken cancellationToken)
    {
        foreach (var element in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case Paragraph paragraph:
                    yield return ReadParagraph(paragraph);
                    break;
                case Table table:
                    yield return new TableBlock(table.Elements<TableRow>()
                        .Select(row => (IReadOnlyList<string>)row.Elements<TableCell>()
                            .Select(cell => string.Join(" ", cell.Elements<Paragraph>().Select(p => ReadParagraph(p).PlainText)).Trim())
                            .ToList())
                        .ToList());
                    break;
            }
        }
    }

    private static ParagraphBlock ReadParagraph(Paragraph paragraph)
    {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
        var heading = style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1
            : style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style.AsSpan(7), out var level) ? Math.Clamp(level, 1, 6)
            : 0;
        var isList = paragraph.ParagraphProperties?.NumberingProperties is not null;

        var spans = new List<Span>();
        foreach (var run in paragraph.Descendants<Run>())
        {
            var bold = run.RunProperties?.Bold is { } b && (b.Val is null || b.Val.Value);
            var italic = run.RunProperties?.Italic is { } i && (i.Val is null || i.Val.Value);
            var text = new StringBuilder();
            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case DocumentFormat.OpenXml.Wordprocessing.Text t:
                        text.Append(t.Text);
                        break;
                    case TabChar:
                        text.Append('\t');
                        break;
                    case Break or CarriageReturn:
                        text.Append('\n');
                        break;
                }
            }

            if (text.Length > 0)
            {
                spans.Add(new Span(text.ToString(), bold, italic));
            }
        }

        return new ParagraphBlock(heading, isList, spans);
    }

    private static string RenderText(IEnumerable<Block> blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ParagraphBlock p:
                    builder.Append(p.IsListItem ? "• " : string.Empty).AppendLine(p.PlainText);
                    if (p.HeadingLevel > 0)
                    {
                        builder.AppendLine();
                    }

                    break;
                case TableBlock t:
                    foreach (var row in t.Rows)
                    {
                        builder.AppendLine(string.Join("\t", row));
                    }

                    builder.AppendLine();
                    break;
            }
        }

        return builder.ToString();
    }

    private static string RenderMarkdown(IEnumerable<Block> blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ParagraphBlock { PlainText.Length: 0 }:
                    continue;
                case ParagraphBlock p when p.HeadingLevel > 0:
                    builder.Append('#', p.HeadingLevel).Append(' ').AppendLine(EscapeMarkdown(p.PlainText)).AppendLine();
                    break;
                case ParagraphBlock p:
                    var text = string.Concat(p.Spans.Select(s =>
                    {
                        var escaped = EscapeMarkdown(s.Text);
                        if (string.IsNullOrWhiteSpace(escaped))
                        {
                            return escaped;
                        }

                        return s.Bold && s.Italic ? $"***{escaped}***" : s.Bold ? $"**{escaped}**" : s.Italic ? $"*{escaped}*" : escaped;
                    }));
                    builder.Append(p.IsListItem ? "- " : string.Empty).AppendLine(text.Replace("\n", "  \n"));
                    if (!p.IsListItem)
                    {
                        builder.AppendLine();
                    }

                    break;
                case TableBlock t when t.Rows.Count > 0:
                    var columns = t.Rows.Max(r => r.Count);
                    for (var r = 0; r < t.Rows.Count; r++)
                    {
                        var cells = Enumerable.Range(0, columns).Select(c => c < t.Rows[r].Count ? EscapeMarkdown(t.Rows[r][c]).Replace("|", "\\|") : string.Empty);
                        builder.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
                        if (r == 0)
                        {
                            builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).AppendLine();
                        }
                    }

                    builder.AppendLine();
                    break;
            }
        }

        return builder.ToString();
    }

    private static string RenderHtml(IEnumerable<Block> blocks)
    {
        var builder = new StringBuilder();
        var inList = false;
        foreach (var block in blocks)
        {
            if (block is ParagraphBlock { IsListItem: true } listItem)
            {
                if (!inList)
                {
                    builder.AppendLine("<ul>");
                    inList = true;
                }

                builder.Append("<li>").Append(RenderSpans(listItem.Spans)).AppendLine("</li>");
                continue;
            }

            if (inList)
            {
                builder.AppendLine("</ul>");
                inList = false;
            }

            switch (block)
            {
                case ParagraphBlock p when p.HeadingLevel > 0:
                    builder.Append("<h").Append(p.HeadingLevel).Append('>').Append(RenderSpans(p.Spans)).Append("</h").Append(p.HeadingLevel).AppendLine(">");
                    break;
                case ParagraphBlock p when p.Spans.Count > 0:
                    builder.Append("<p>").Append(RenderSpans(p.Spans)).AppendLine("</p>");
                    break;
                case TableBlock t:
                    builder.AppendLine("<table>");
                    foreach (var row in t.Rows)
                    {
                        builder.Append("<tr>");
                        foreach (var cell in row)
                        {
                            builder.Append("<td>").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
                        }

                        builder.AppendLine("</tr>");
                    }

                    builder.AppendLine("</table>");
                    break;
            }
        }

        if (inList)
        {
            builder.AppendLine("</ul>");
        }

        return builder.ToString();
    }

    private static string RenderSpans(IEnumerable<Span> spans) =>
        string.Concat(spans.Select(s =>
        {
            var html = WebUtility.HtmlEncode(s.Text).Replace("\n", "<br>");
            if (s.Italic)
            {
                html = $"<em>{html}</em>";
            }

            return s.Bold ? $"<strong>{html}</strong>" : html;
        }));

    private static string EscapeMarkdown(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
