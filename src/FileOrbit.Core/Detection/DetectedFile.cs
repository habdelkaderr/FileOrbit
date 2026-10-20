using FileOrbit.Core.Formats;

namespace FileOrbit.Core.Detection;

public enum DetectionMethod
{
    /// <summary>Identified from magic bytes at a fixed offset.</summary>
    Signature,

    /// <summary>Identified by inspecting the inside of a container (e.g. the parts of a ZIP package).</summary>
    Container,

    /// <summary>Identified by sniffing textual content.</summary>
    Content,

    /// <summary>Content was inconclusive so the file extension was trusted.</summary>
    Extension,

    Directory,

    Unknown,
}

/// <summary>A file or folder whose real type has been determined.</summary>
public sealed record DetectedFile(
    string Path,
    FileFormat Format,
    DetectionMethod Method,
    long SizeBytes,
    string? Warning = null)
{
    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));

    public string Extension => Format == KnownFormats.Folder ? string.Empty : System.IO.Path.GetExtension(Path);

    public bool IsDirectory => Format == KnownFormats.Folder;

    /// <summary>File name without the format's extension (handles compound extensions like .tar.gz).</summary>
    public string BaseName
    {
        get
        {
            var name = FileName;
            foreach (var extension in Format.Extensions.OrderByDescending(e => e.Length))
            {
                if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^extension.Length];
                }
            }

            return IsDirectory ? name : System.IO.Path.GetFileNameWithoutExtension(name);
        }
    }
}
