namespace FileOrbit.Engines.Processes;

/// <summary>
/// Finds external tools. Only absolute paths to existing .exe files are ever returned; the search
/// never includes the current directory, so a planted executable next to an input file is not used.
/// </summary>
public sealed class ExecutableLocator
{
    private readonly IReadOnlyDictionary<string, string> _overrides;

    public ExecutableLocator(IReadOnlyDictionary<string, string>? overrides = null) =>
        _overrides = overrides ?? new Dictionary<string, string>();

    /// <summary>
    /// Locates an executable. Order: user override for <paramref name="engineId"/>, well-known install
    /// locations, then PATH (absolute entries only).
    /// </summary>
    public string? Find(string engineId, string exeName, IEnumerable<string> knownLocations)
    {
        if (_overrides.TryGetValue(engineId, out var configured) && IsValidExecutable(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var location in knownLocations)
        {
            var expanded = Environment.ExpandEnvironmentVariables(location);
            if (expanded.Contains('*'))
            {
                foreach (var match in ExpandWildcard(expanded))
                {
                    if (IsValidExecutable(match))
                    {
                        return match;
                    }
                }
            }
            else if (IsValidExecutable(expanded))
            {
                return Path.GetFullPath(expanded);
            }
        }

        return FindOnPath(exeName);
    }

    public static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = directory.Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
            {
                continue; // Relative PATH entries resolve against the working directory: unsafe.
            }

            var candidate = Path.Combine(trimmed, exeName);
            if (IsValidExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsValidExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    /// <summary>Expands a single "*" directory segment, e.g. "C:\Tools\ffmpeg-*\bin\ffmpeg.exe".</summary>
    private static IEnumerable<string> ExpandWildcard(string pattern)
    {
        var index = pattern.IndexOf('*');
        var separatorBefore = pattern.LastIndexOf(Path.DirectorySeparatorChar, index);
        var separatorAfter = pattern.IndexOf(Path.DirectorySeparatorChar, index);
        if (separatorBefore < 0 || separatorAfter < 0)
        {
            yield break;
        }

        var parent = pattern[..separatorBefore];
        var segmentPattern = pattern[(separatorBefore + 1)..separatorAfter];
        var rest = pattern[(separatorAfter + 1)..];
        if (!Directory.Exists(parent))
        {
            yield break;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(parent, segmentPattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories.OrderDescending(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(directory, rest);
            if (candidate.Contains('*'))
            {
                foreach (var nested in ExpandWildcard(candidate))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return candidate;
            }
        }
    }
}
