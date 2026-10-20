using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Planning;

namespace FileOrbit.Engines.Archives;

/// <summary>Limits that protect against malicious archives (zip slip, decompression bombs, links).</summary>
public sealed class ArchiveLimits
{
    public static ArchiveLimits Default { get; } = new();

    public long MaxTotalBytes { get; init; } = 64L * 1024 * 1024 * 1024;

    public int MaxEntries { get; init; } = 500_000;

    /// <summary>Abort when output exceeds this multiple of the archive size (and at least <see cref="RatioFloorBytes"/>).</summary>
    public double MaxCompressionRatio { get; init; } = 250;

    public long RatioFloorBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>Tracks extraction volume and aborts when an archive looks like a decompression bomb.</summary>
internal sealed class ExtractionBudget(long archiveBytes, ArchiveLimits limits)
{
    private long _bytes;
    private int _entries;

    public long BytesWritten => _bytes;

    public void AddEntry()
    {
        if (++_entries > limits.MaxEntries)
        {
            throw new ConversionException($"The archive contains more than {limits.MaxEntries:N0} entries, which FileOrbit treats as unsafe.");
        }
    }

    public void AddBytes(long count)
    {
        _bytes += count;
        if (_bytes > limits.MaxTotalBytes)
        {
            throw new ConversionException("The archive expands to more data than FileOrbit's safety limit allows.");
        }

        if (_bytes > limits.RatioFloorBytes && _bytes > archiveBytes * limits.MaxCompressionRatio)
        {
            throw new ConversionException("The archive expands far more than a normal archive would (possible decompression bomb). Extraction was stopped.");
        }
    }
}

internal static class ArchiveSafety
{
    /// <summary>Copies a stream while enforcing the budget and honouring cancellation.</summary>
    public static async Task CopyWithBudgetAsync(Stream source, Stream destination, ExtractionBudget budget, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            budget.AddBytes(read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After an external tool extracted into <paramref name="root"/>, removes symbolic links and
    /// junctions, and verifies nothing escaped the folder.
    /// </summary>
    public static int RemoveReparsePoints(string root)
    {
        var removed = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (!PathSafety.IsWithin(root, entry.FullName))
                {
                    throw new ConversionException("The archive tried to write outside the destination folder.");
                }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (entry is DirectoryInfo linkDirectory)
                    {
                        linkDirectory.Delete(); // Deletes the link only, never its target.
                    }
                    else
                    {
                        entry.Delete();
                    }

                    removed++;
                }
                else if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                }
            }
        }

        return removed;
    }

    /// <summary>Lists files under a folder with paths relative to it, for archive creation.</summary>
    public static IEnumerable<(string FullPath, string EntryName)> EnumerateForArchive(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            yield return (sourcePath, Path.GetFileName(sourcePath));
            yield break;
        }

        var baseName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // Never follow links out of the folder.
            IgnoreInaccessible = false,
        };
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", options))
        {
            yield return (file, baseName + "/" + Path.GetRelativePath(sourcePath, file).Replace(Path.DirectorySeparatorChar, '/'));
        }
    }
}
