using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Formats;
using FileOrbit.Core.Radial;

namespace FileOrbit.App.Theming;

/// <summary>Segoe Fluent Icons code points for categories and actions.</summary>
public static class Glyphs
{
    public const string FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public const string Document = "";
    public const string Pdf = "";
    public const string Spreadsheet = "";
    public const string Presentation = "";
    public const string Image = "";
    public const string Audio = "";
    public const string Video = "";
    public const string Archive = "";
    public const string Extract = "";
    public const string TextData = "";
    public const string Text = "";
    public const string Folder = "";
    public const string More = "";
    public const string Back = "";
    public const string Resize = "";
    public const string Compress = "";
    public const string Ocr = "";
    public const string Pin = "";
    public const string Warning = "";
    public const string Settings = "";
    public const string Home = "";
    public const string Queue = "";
    public const string History = "";
    public const string Catalog = "";
    public const string Engines = "";
    public const string Files = "";
    public const string Check = "";
    public const string Error = "";
    public const string Cancel = "";
    public const string OpenFile = "";
    public const string OpenFolder = "";
    public const string Refresh = "";
    public const string Lock = "";
    public const string Shield = "";

    public static string ForCategory(FormatCategory category) => category switch
    {
        FormatCategory.Document => Document,
        FormatCategory.Spreadsheet => Spreadsheet,
        FormatCategory.Presentation => Presentation,
        FormatCategory.Image => Image,
        FormatCategory.Audio => Audio,
        FormatCategory.Video => Video,
        FormatCategory.Archive => Archive,
        FormatCategory.TextData => TextData,
        FormatCategory.Folder => Folder,
        _ => More,
    };

    public static string ForFormat(FileFormat format) => format.Id switch
    {
        "pdf" => Pdf,
        "txt" or "md" or "rtf" => Text,
        _ => ForCategory(format.Category),
    };

    public static string ForItem(RadialItem item)
    {
        if (item.Kind == RadialItemKind.More)
        {
            return More;
        }

        if (item.Kind == RadialItemKind.Category || item.Action is null)
        {
            return ForCategory(item.Category);
        }

        var action = item.Action;
        return action.Kind switch
        {
            ActionKind.Extract => Extract,
            ActionKind.Compress => Compress,
            ActionKind.Transform when action.Variant == "resized" => Resize,
            ActionKind.Transform => Compress,
            _ when action.Variant == "ocr" => Ocr,
            _ => ForFormat(action.Target),
        };
    }

    public static string CategoryBrushKey(FormatCategory category) => category switch
    {
        FormatCategory.Document => "CategoryDocumentBrush",
        FormatCategory.Spreadsheet => "CategorySpreadsheetBrush",
        FormatCategory.Presentation => "CategoryPresentationBrush",
        FormatCategory.Image => "CategoryImageBrush",
        FormatCategory.Audio => "CategoryAudioBrush",
        FormatCategory.Video => "CategoryVideoBrush",
        FormatCategory.Archive => "CategoryArchiveBrush",
        FormatCategory.TextData => "CategoryTextDataBrush",
        _ => "CategoryOtherBrush",
    };
}
