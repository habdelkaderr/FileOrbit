using System.Text;

namespace FileOrbit.Core.Planning;

/// <summary>Validation helpers that treat every path coming from outside as untrusted.</summary>
public static class PathSafety
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Normalizes a user-supplied path to an absolute, local or UNC file system path.
    /// Rejects device paths, alternate data streams and relative paths.
    /// </summary>
    public static string NormalizeInputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UnsafePathException("No path was supplied.");
        }

        path = path.Trim().Trim('"');
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new UnsafePathException("The path contains invalid characters.");
        }

        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafePathException("Device paths are not supported.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new UnsafePathException("Only absolute paths are accepted.");
        }

        var full = Path.GetFullPath(path);

        // A colon after the drive designator would address an NTFS alternate data stream.
        var withoutRoot = full[Path.GetPathRoot(full)!.Length..];
        if (withoutRoot.Contains(':'))
        {
            throw new UnsafePathException("Alternate data streams are not supported.");
        }

        foreach (var segment in withoutRoot.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsReservedName(segment))
            {
                throw new UnsafePathException($"'{segment}' is a reserved Windows device name.");
            }
        }

        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    /// <summary>Validates that a path is an existing regular file or folder that can be used as input.</summary>
    public static string ValidateInput(string path, bool allowDirectory)
    {
        var full = NormalizeInputPath(path);
        if (File.Exists(full))
        {
            var attributes = File.GetAttributes(full);
            if (attributes.HasFlag(FileAttributes.Offline))
            {
                throw new UnsafePathException("The file is offline (not downloaded). Open it once to download it, then try again.");
            }

            return full;
        }

        if (Directory.Exists(full))
        {
            if (!allowDirectory)
            {
                throw new UnsafePathException("A folder was supplied where a file is expected.");
            }

            return full;
        }

        throw new UnsafePathException("The file no longer exists.");
    }

    /// <summary>Validates a destination folder; it must be a writable, non-system location.</summary>
    public static string ValidateOutputDirectory(string path)
    {
        var full = NormalizeInputPath(path);
        foreach (var protectedRoot in ProtectedRoots())
        {
            if (IsWithin(protectedRoot, full))
            {
                throw new UnsafePathException("FileOrbit will not write into Windows or program installation folders.");
            }
        }

        return full;
    }

    /// <summary>Removes characters that are not allowed in Windows file names and avoids reserved names.</summary>
    public static string SanitizeFileName(string name, string fallback = "output")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result.All(c => c == '.'))
        {
            result = fallback;
        }

        if (IsReservedName(result))
        {
            result = "_" + result;
        }

        // Keep well within MAX_PATH-era limits for a single component.
        return result.Length > 150 ? result[..150].TrimEnd('.', ' ') : result;
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or inside it.</summary>
    public static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves an archive entry name under <paramref name="destination"/>, rejecting absolute paths,
    /// drive letters and ".." traversal ("zip slip").
    /// </summary>
    public static string ResolveArchiveEntry(string destination, string entryName)
    {
        var normalized = entryName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new UnsafePathException($"Archive entry '{entryName}' has an unsafe path.");
        }

        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".." || IsReservedName(s)))
        {
            throw new UnsafePathException($"Archive entry '{entryName}' has an unsafe path.");
        }

        var target = Path.GetFullPath(Path.Combine(destination, normalized));
        if (!IsWithin(destination, target))
        {
            throw new UnsafePathException($"Archive entry '{entryName}' would be written outside the destination folder.");
        }

        return target;
    }

    public static bool IsSamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsReservedName(string segment)
    {
        var stem = segment.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem);
    }

    private static IEnumerable<string> ProtectedRoots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
            {
                yield return path;
            }
        }
    }
}

public sealed class UnsafePathException : Exception
{
    public UnsafePathException(string message)
        : base(message)
    {
    }
}
