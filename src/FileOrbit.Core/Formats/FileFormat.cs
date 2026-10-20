namespace FileOrbit.Core.Formats;

public enum FormatCategory
{
    Document,
    Spreadsheet,
    Presentation,
    Image,
    Audio,
    Video,
    Archive,
    TextData,
    Folder,
    Other,
}

/// <summary>
/// Describes a file format FileOrbit understands. Formats are data only; what can be done with
/// them is declared separately by conversion engines through <see cref="Capabilities.ConversionCapability"/>.
/// </summary>
public sealed class FileFormat : IEquatable<FileFormat>
{
    public FileFormat(string id, string name, string fullName, FormatCategory category, string mimeType, params string[] extensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id.ToLowerInvariant();
        Name = name;
        FullName = fullName;
        Category = category;
        MimeType = mimeType;
        Extensions = extensions.Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()).ToArray();
    }

    /// <summary>Stable lowercase identifier, e.g. "pdf", "jpeg", "tgz".</summary>
    public string Id { get; }

    /// <summary>Short label shown in the radial menu, e.g. "PDF".</summary>
    public string Name { get; }

    /// <summary>Full name for tooltips, e.g. "Portable Document Format".</summary>
    public string FullName { get; }

    public FormatCategory Category { get; }

    public string MimeType { get; }

    /// <summary>Extensions including the leading dot, primary first.</summary>
    public IReadOnlyList<string> Extensions { get; }

    public string PrimaryExtension => Extensions.Count > 0 ? Extensions[0] : string.Empty;

    /// <summary>True for formats whose payload is human-readable text.</summary>
    public bool IsText => Category == FormatCategory.TextData || Id is "svg" or "rtf";

    public bool Equals(FileFormat? other) => other is not null && other.Id == Id;

    public override bool Equals(object? obj) => obj is FileFormat other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Id;
}
