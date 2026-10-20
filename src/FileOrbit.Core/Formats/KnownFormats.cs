namespace FileOrbit.Core.Formats;

/// <summary>The built-in set of formats. Additional formats can be registered on <see cref="FormatCatalog"/>.</summary>
public static class KnownFormats
{
    // Documents
    public static readonly FileFormat Pdf = new("pdf", "PDF", "Portable Document Format", FormatCategory.Document, "application/pdf", ".pdf");
    public static readonly FileFormat Docx = new("docx", "DOCX", "Microsoft Word document", FormatCategory.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx");
    public static readonly FileFormat Doc = new("doc", "DOC", "Word 97-2003 document", FormatCategory.Document, "application/msword", ".doc");
    public static readonly FileFormat Odt = new("odt", "ODT", "OpenDocument text", FormatCategory.Document, "application/vnd.oasis.opendocument.text", ".odt");
    public static readonly FileFormat Rtf = new("rtf", "RTF", "Rich Text Format", FormatCategory.Document, "application/rtf", ".rtf");

    // Spreadsheets
    public static readonly FileFormat Xlsx = new("xlsx", "XLSX", "Microsoft Excel workbook", FormatCategory.Spreadsheet, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx");
    public static readonly FileFormat Xls = new("xls", "XLS", "Excel 97-2003 workbook", FormatCategory.Spreadsheet, "application/vnd.ms-excel", ".xls");
    public static readonly FileFormat Ods = new("ods", "ODS", "OpenDocument spreadsheet", FormatCategory.Spreadsheet, "application/vnd.oasis.opendocument.spreadsheet", ".ods");

    // Presentations
    public static readonly FileFormat Pptx = new("pptx", "PPTX", "Microsoft PowerPoint presentation", FormatCategory.Presentation, "application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx");
    public static readonly FileFormat Ppt = new("ppt", "PPT", "PowerPoint 97-2003 presentation", FormatCategory.Presentation, "application/vnd.ms-powerpoint", ".ppt");
    public static readonly FileFormat Odp = new("odp", "ODP", "OpenDocument presentation", FormatCategory.Presentation, "application/vnd.oasis.opendocument.presentation", ".odp");

    // Text and data
    public static readonly FileFormat Txt = new("txt", "TXT", "Plain text", FormatCategory.TextData, "text/plain", ".txt", ".text", ".log");
    public static readonly FileFormat Markdown = new("md", "Markdown", "Markdown text", FormatCategory.TextData, "text/markdown", ".md", ".markdown");
    public static readonly FileFormat Html = new("html", "HTML", "Web page (HTML)", FormatCategory.TextData, "text/html", ".html", ".htm");
    public static readonly FileFormat Csv = new("csv", "CSV", "Comma-separated values", FormatCategory.TextData, "text/csv", ".csv");
    public static readonly FileFormat Tsv = new("tsv", "TSV", "Tab-separated values", FormatCategory.TextData, "text/tab-separated-values", ".tsv", ".tab");
    public static readonly FileFormat Json = new("json", "JSON", "JavaScript Object Notation", FormatCategory.TextData, "application/json", ".json");

    // Images
    public static readonly FileFormat Png = new("png", "PNG", "Portable Network Graphics", FormatCategory.Image, "image/png", ".png");
    public static readonly FileFormat Jpeg = new("jpeg", "JPEG", "JPEG image", FormatCategory.Image, "image/jpeg", ".jpg", ".jpeg", ".jpe", ".jfif");
    public static readonly FileFormat WebP = new("webp", "WebP", "WebP image", FormatCategory.Image, "image/webp", ".webp");
    public static readonly FileFormat Tiff = new("tiff", "TIFF", "Tagged Image File Format", FormatCategory.Image, "image/tiff", ".tif", ".tiff");
    public static readonly FileFormat Bmp = new("bmp", "BMP", "Windows bitmap", FormatCategory.Image, "image/bmp", ".bmp", ".dib");
    public static readonly FileFormat Gif = new("gif", "GIF", "Graphics Interchange Format", FormatCategory.Image, "image/gif", ".gif");
    public static readonly FileFormat Ico = new("ico", "ICO", "Windows icon", FormatCategory.Image, "image/x-icon", ".ico");
    public static readonly FileFormat Heic = new("heic", "HEIC", "High Efficiency Image (HEIF)", FormatCategory.Image, "image/heic", ".heic", ".heif", ".hif");
    public static readonly FileFormat Avif = new("avif", "AVIF", "AV1 image", FormatCategory.Image, "image/avif", ".avif");
    public static readonly FileFormat Svg = new("svg", "SVG", "Scalable Vector Graphics", FormatCategory.Image, "image/svg+xml", ".svg");

    // Audio
    public static readonly FileFormat Mp3 = new("mp3", "MP3", "MPEG audio layer III", FormatCategory.Audio, "audio/mpeg", ".mp3");
    public static readonly FileFormat Wav = new("wav", "WAV", "Waveform audio", FormatCategory.Audio, "audio/wav", ".wav");
    public static readonly FileFormat Flac = new("flac", "FLAC", "Free Lossless Audio Codec", FormatCategory.Audio, "audio/flac", ".flac");
    public static readonly FileFormat M4a = new("m4a", "M4A", "AAC audio (MPEG-4)", FormatCategory.Audio, "audio/mp4", ".m4a");
    public static readonly FileFormat Aac = new("aac", "AAC", "Raw AAC audio (ADTS)", FormatCategory.Audio, "audio/aac", ".aac");
    public static readonly FileFormat Ogg = new("ogg", "OGG", "Ogg Vorbis audio", FormatCategory.Audio, "audio/ogg", ".ogg", ".oga");
    public static readonly FileFormat Opus = new("opus", "Opus", "Opus audio", FormatCategory.Audio, "audio/opus", ".opus");
    public static readonly FileFormat Wma = new("wma", "WMA", "Windows Media Audio", FormatCategory.Audio, "audio/x-ms-wma", ".wma");

    // Video
    public static readonly FileFormat Mp4 = new("mp4", "MP4", "MPEG-4 video", FormatCategory.Video, "video/mp4", ".mp4", ".m4v");
    public static readonly FileFormat Mkv = new("mkv", "MKV", "Matroska video", FormatCategory.Video, "video/x-matroska", ".mkv");
    public static readonly FileFormat Mov = new("mov", "MOV", "QuickTime movie", FormatCategory.Video, "video/quicktime", ".mov");
    public static readonly FileFormat WebM = new("webm", "WebM", "WebM video", FormatCategory.Video, "video/webm", ".webm");
    public static readonly FileFormat Avi = new("avi", "AVI", "Audio Video Interleave", FormatCategory.Video, "video/x-msvideo", ".avi");
    public static readonly FileFormat Wmv = new("wmv", "WMV", "Windows Media Video", FormatCategory.Video, "video/x-ms-wmv", ".wmv");

    // Archives
    public static readonly FileFormat Zip = new("zip", "ZIP", "ZIP archive", FormatCategory.Archive, "application/zip", ".zip");
    public static readonly FileFormat SevenZip = new("7z", "7Z", "7-Zip archive", FormatCategory.Archive, "application/x-7z-compressed", ".7z");
    public static readonly FileFormat Tar = new("tar", "TAR", "Tape archive", FormatCategory.Archive, "application/x-tar", ".tar");
    public static readonly FileFormat Gz = new("gz", "GZ", "Gzip-compressed file", FormatCategory.Archive, "application/gzip", ".gz");
    public static readonly FileFormat TarGz = new("tgz", "TAR.GZ", "Gzip-compressed tape archive", FormatCategory.Archive, "application/gzip", ".tar.gz", ".tgz");
    public static readonly FileFormat Rar = new("rar", "RAR", "RAR archive", FormatCategory.Archive, "application/vnd.rar", ".rar");

    // Pseudo formats
    public static readonly FileFormat Folder = new("folder", "Folder", "Folder", FormatCategory.Folder, "inode/directory");
    public static readonly FileFormat Unknown = new("unknown", "File", "Unrecognised file", FormatCategory.Other, "application/octet-stream");

    public static IEnumerable<FileFormat> All =>
    [
        Pdf, Docx, Doc, Odt, Rtf,
        Xlsx, Xls, Ods,
        Pptx, Ppt, Odp,
        Txt, Markdown, Html, Csv, Tsv, Json,
        Png, Jpeg, WebP, Tiff, Bmp, Gif, Ico, Heic, Avif, Svg,
        Mp3, Wav, Flac, M4a, Aac, Ogg, Opus, Wma,
        Mp4, Mkv, Mov, WebM, Avi, Wmv,
        Zip, SevenZip, Tar, Gz, TarGz, Rar,
        Folder, Unknown,
    ];
}
