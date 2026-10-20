using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace FileOrbit.Engines.Text;

/// <summary>HTML helpers: Markdown rendering, remote-resource stripping and text extraction.</summary>
public static partial class HtmlTools
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml() // Raw HTML in Markdown is not passed through (no script injection into outputs).
        .Build();

    public static string MarkdownToHtmlDocument(string markdown, string title)
    {
        var body = Markdown.ToHtml(markdown, Pipeline);
        return WrapDocument(body, title);
    }

    public static string MarkdownToPlainText(string markdown) => Markdown.ToPlainText(markdown, Pipeline);

    public static string WrapDocument(string body, string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>");
        builder.AppendLine("<style>body{font-family:'Segoe UI',system-ui,sans-serif;max-width:48rem;margin:2rem auto;padding:0 1rem;line-height:1.6;color:#1b1b1b}" +
                           "pre,code{font-family:Consolas,monospace;background:#f4f4f4;border-radius:4px}pre{padding:.75rem;overflow:auto}" +
                           "table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:.3rem .6rem}blockquote{border-left:4px solid #ccc;margin-left:0;padding-left:1rem;color:#555}</style>");
        builder.AppendLine("</head><body>");
        builder.Append(body);
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    public static string TextToHtmlDocument(string text, string title)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n");
        var body = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                continue;
            }

            body.Append("<p>").Append(WebUtility.HtmlEncode(paragraph.Trim()).Replace("\n", "<br>\n")).AppendLine("</p>");
        }

        return WrapDocument(body.ToString(), title);
    }

    /// <summary>
    /// Removes references to remote resources (http, https, file, UNC) so that rendering the HTML
    /// with another engine (e.g. Word) never contacts the network. Returns the number removed.
    /// </summary>
    public static string StripRemoteResources(string html, out int removed)
    {
        var count = 0;
        var result = RemoteReference().Replace(html, match =>
        {
            count++;
            return match.Groups["attr"].Value + "=\"\"";
        });
        result = ScriptBlock().Replace(result, _ =>
        {
            count++;
            return string.Empty;
        });
        result = CssImport().Replace(result, _ =>
        {
            count++;
            return string.Empty;
        });
        removed = count;
        return result;
    }

    public static string HtmlToText(string html)
    {
        var text = ScriptOrStyle().Replace(html, " ");
        text = Comment().Replace(text, " ");
        text = BlockBreak().Replace(text, "\n");
        text = ListItem().Replace(text, "\n• ");
        text = Tag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = HorizontalSpace().Replace(text, " ");
        text = SpaceAroundNewline().Replace(text, "\n");
        text = ManyNewlines().Replace(text, "\n\n");
        return text.Trim() + Environment.NewLine;
    }

    // src/href/poster/data/background attributes pointing at remote or local-file resources.
    [GeneratedRegex("""(?<attr>\b(?:src|href|poster|data|background|srcset|xlink:href))\s*=\s*(?<q>["'])\s*(?:https?:|ftp:|file:|//|\\\\)[^"']*\k<q>""", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteReference();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlock();

    [GeneratedRegex(@"@import\s+(?:url\()?\s*['""]?(?:https?:|//)[^;]*;", RegexOptions.IgnoreCase)]
    private static partial Regex CssImport();

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<\s*(br|/p|/div|/h[1-6]|/tr|/table|/blockquote|/pre|/section|/article|hr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpaceAroundNewline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();
}
