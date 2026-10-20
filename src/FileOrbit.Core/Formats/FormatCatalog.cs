namespace FileOrbit.Core.Formats;

/// <summary>
/// Central lookup of formats by id and extension. New formats can be registered at startup
/// without touching the UI or the detector.
/// </summary>
public sealed class FormatCatalog
{
    private readonly Dictionary<string, FileFormat> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileFormat> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _popularTargets = new(StringComparer.OrdinalIgnoreCase);

    public static FormatCatalog CreateDefault()
    {
        var catalog = new FormatCatalog();
        foreach (var format in KnownFormats.All)
        {
            catalog.Register(format);
        }

        // Ordered "most people want this" targets, used to rank the first ring of the radial menu.
        catalog.SetPopularTargets("pdf", "docx", "png", "jpeg", "txt", "xlsx", "pptx", "html", "tiff");
        catalog.SetPopularTargets("docx", "pdf", "txt", "html", "odt", "rtf", "png");
        catalog.SetPopularTargets("doc", "docx", "pdf", "txt");
        catalog.SetPopularTargets("odt", "docx", "pdf", "txt");
        catalog.SetPopularTargets("rtf", "docx", "pdf", "txt");
        catalog.SetPopularTargets("xlsx", "pdf", "csv", "json", "html", "ods");
        catalog.SetPopularTargets("xls", "xlsx", "pdf", "csv");
        catalog.SetPopularTargets("ods", "xlsx", "pdf", "csv");
        catalog.SetPopularTargets("csv", "xlsx", "json", "tsv", "html");
        catalog.SetPopularTargets("tsv", "csv", "xlsx", "json");
        catalog.SetPopularTargets("json", "csv", "xlsx");
        catalog.SetPopularTargets("pptx", "pdf", "png", "jpeg", "mp4", "odp");
        catalog.SetPopularTargets("ppt", "pptx", "pdf", "png");
        catalog.SetPopularTargets("odp", "pptx", "pdf");
        catalog.SetPopularTargets("md", "html", "pdf", "docx", "txt");
        catalog.SetPopularTargets("html", "pdf", "txt", "docx", "md");
        catalog.SetPopularTargets("txt", "pdf", "docx", "html");
        catalog.SetPopularTargets("png", "jpeg", "webp", "ico", "pdf", "bmp", "tiff", "gif");
        catalog.SetPopularTargets("jpeg", "png", "webp", "pdf", "tiff", "bmp", "ico");
        catalog.SetPopularTargets("webp", "png", "jpeg", "gif");
        catalog.SetPopularTargets("heic", "jpeg", "png", "webp");
        catalog.SetPopularTargets("avif", "jpeg", "png", "webp");
        catalog.SetPopularTargets("svg", "png", "jpeg", "webp", "ico", "pdf");
        catalog.SetPopularTargets("gif", "png", "mp4", "webp");
        catalog.SetPopularTargets("tiff", "jpeg", "png", "pdf");
        catalog.SetPopularTargets("bmp", "png", "jpeg");
        catalog.SetPopularTargets("ico", "png");
        catalog.SetPopularTargets("mp3", "wav", "m4a", "flac", "ogg");
        catalog.SetPopularTargets("wav", "mp3", "flac", "m4a");
        catalog.SetPopularTargets("flac", "mp3", "wav", "m4a");
        catalog.SetPopularTargets("m4a", "mp3", "wav", "flac");
        catalog.SetPopularTargets("aac", "mp3", "m4a", "wav");
        catalog.SetPopularTargets("ogg", "mp3", "wav");
        catalog.SetPopularTargets("opus", "mp3", "wav");
        catalog.SetPopularTargets("wma", "mp3", "wav");
        catalog.SetPopularTargets("mp4", "mp3", "webm", "mkv", "mov", "m4a");
        catalog.SetPopularTargets("mkv", "mp4", "mp3", "webm");
        catalog.SetPopularTargets("mov", "mp4", "mp3", "webm");
        catalog.SetPopularTargets("webm", "mp4", "mp3");
        catalog.SetPopularTargets("avi", "mp4", "mp3");
        catalog.SetPopularTargets("wmv", "mp4", "mp3");
        catalog.SetPopularTargets("zip", "folder", "7z", "tgz");
        catalog.SetPopularTargets("7z", "folder", "zip");
        catalog.SetPopularTargets("rar", "folder", "zip");
        catalog.SetPopularTargets("tar", "folder", "zip", "tgz");
        catalog.SetPopularTargets("tgz", "folder", "zip");
        catalog.SetPopularTargets("gz", "folder");
        catalog.SetPopularTargets("folder", "zip", "7z", "tgz");
        return catalog;
    }

    public IReadOnlyCollection<FileFormat> Formats => _byId.Values;

    public void Register(FileFormat format)
    {
        _byId[format.Id] = format;
        foreach (var extension in format.Extensions)
        {
            _byExtension.TryAdd(extension, format);
        }
    }

    public void SetPopularTargets(string sourceId, params string[] targetIds) => _popularTargets[sourceId] = targetIds;

    /// <summary>Lower is more popular; <see cref="int.MaxValue"/> when not listed.</summary>
    public int GetPopularityRank(string sourceId, string targetId)
    {
        if (_popularTargets.TryGetValue(sourceId, out var targets))
        {
            var index = Array.FindIndex(targets, t => string.Equals(t, targetId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    public FileFormat? FindById(string id) => _byId.GetValueOrDefault(id);

    public FileFormat GetById(string id) =>
        _byId.TryGetValue(id, out var format) ? format : throw new KeyNotFoundException($"Unknown format id '{id}'.");

    /// <summary>Finds a format by file name, honouring compound extensions such as ".tar.gz".</summary>
    public FileFormat? FindByFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        // Walk from the longest possible compound extension to the shortest.
        for (var dot = name.IndexOf('.'); dot >= 0 && dot < name.Length - 1; dot = name.IndexOf('.', dot + 1))
        {
            if (_byExtension.TryGetValue(name[dot..], out var format))
            {
                return format;
            }
        }

        return null;
    }
}
