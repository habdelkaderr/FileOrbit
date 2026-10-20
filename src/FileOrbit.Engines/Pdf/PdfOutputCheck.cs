using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Formats;
using FileOrbit.Core.Jobs;
using UglyToad.PdfPig;

namespace FileOrbit.Engines.Pdf;

/// <summary>
/// Rejects PDFs that cannot be opened or have no pages. Office, for example, "exports" an empty
/// workbook as a structurally valid PDF with zero pages, which viewers report as damaged.
/// </summary>
public sealed class PdfOutputCheck : IOutputContentCheck
{
    public bool AppliesTo(FileFormat format) => format == KnownFormats.Pdf;

    public void Check(string path)
    {
        int pages;
        try
        {
            using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            pages = document.NumberOfPages;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConversionException("The engine produced a PDF that cannot be opened.", ex.Message, ex);
        }

        if (pages == 0)
        {
            throw new ConversionException(
                "The result has no pages, because the source has nothing to print (for example an empty workbook or document). Add content and try again.",
                "Output PDF has 0 pages.");
        }
    }
}
