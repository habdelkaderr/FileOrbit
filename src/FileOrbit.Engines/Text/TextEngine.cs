using System.Text;
using FileOrbit.Core.Capabilities;

namespace FileOrbit.Engines.Text;

/// <summary>Built-in conversions between Markdown, HTML and plain text (Markdig, BSD-2).</summary>
public sealed class TextEngine : ConversionEngineBase
{
    public const string EngineId = "text";
    public const long MaxInputBytes = 128L * 1024 * 1024;

    public override string Id => EngineId;

    public override string DisplayName => "Text and Markdown (Markdig)";

    public override string Description => "Converts Markdown, HTML and plain text. Built in.";

    protected override EngineStatus CheckStatus() => EngineStatus.Available("Ready (built in)");

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        yield return Cap("md", "html", Fidelity.Lossless, quality: 80) with { Note = "Raw HTML embedded in the Markdown is not passed through" };
        yield return Cap("md", "txt", Fidelity.Extraction, quality: 80);
        yield return Cap("html", "txt", Fidelity.Extraction, quality: 70) with { Note = "Keeps the readable text; scripts and styles are dropped" };
        yield return Cap("txt", "html", Fidelity.Lossless, quality: 80);
        yield return Cap("txt", "md", Fidelity.Lossless, quality: 50) with { Note = "Copies the text into a Markdown file" };
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        if (context.Input.SizeBytes > MaxInputBytes)
        {
            throw new ConversionException("The file is too large for text conversion (limit 128 MB).");
        }

        context.Report(0.1, "Reading");
        var text = await File.ReadAllTextAsync(context.Input.Path, cancellationToken);
        var title = Path.GetFileNameWithoutExtension(context.Input.Path);
        var output = (context.Input.Format.Id, context.Target.Id) switch
        {
            ("md", "html") => HtmlTools.MarkdownToHtmlDocument(text, title),
            ("md", "txt") => HtmlTools.MarkdownToPlainText(text),
            ("html", "txt") => HtmlTools.HtmlToText(text),
            ("txt", "html") => HtmlTools.TextToHtmlDocument(text, title),
            ("txt", "md") => text,
            _ => throw new ConversionException($"Cannot convert {context.Input.Format.Name} to {context.Target.Name}."),
        };

        await File.WriteAllTextAsync(context.PrimaryOutputPath, output, new UTF8Encoding(context.Target.Id == "txt"), cancellationToken);
        context.Report(1, "Done");
    }
}
