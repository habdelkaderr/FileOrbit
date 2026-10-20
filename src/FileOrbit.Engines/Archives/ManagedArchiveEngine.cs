using System.Formats.Tar;
using System.IO.Compression;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Planning;

namespace FileOrbit.Engines.Archives;

/// <summary>
/// ZIP, TAR, TAR.GZ and GZ handling with the .NET base libraries. Every entry path is validated
/// (no traversal, no absolute paths), links are skipped, and extraction volume is bounded.
/// </summary>
public sealed class ManagedArchiveEngine : ConversionEngineBase
{
    public const string EngineId = "dotnet-archive";
    private readonly ArchiveLimits _limits;

    public ManagedArchiveEngine(ArchiveLimits? limits = null) => _limits = limits ?? ArchiveLimits.Default;

    public override string Id => EngineId;

    public override string DisplayName => "Built-in archives (.NET)";

    public override string Description => "Creates and extracts ZIP, TAR, TAR.GZ and GZ archives without external software.";

    protected override EngineStatus CheckStatus() => EngineStatus.Available("Ready (built in)");

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var source in new[] { "zip", "tar", "tgz", "gz" })
        {
            yield return Cap(source, "folder", Fidelity.Repackage, quality: 70) with
            {
                Action = ActionKind.Extract,
                Shape = OutputShape.Folder,
                Note = source == "gz" ? "Decompresses the file next to the original" : "Extracts into a new folder; existing files are never overwritten",
                Cost = ResourceCost.Medium,
            };
        }

        foreach (var (source, target) in new[] { ("zip", "tgz"), ("zip", "tar"), ("tar", "zip"), ("tar", "tgz"), ("tgz", "zip"), ("tgz", "tar") })
        {
            yield return Cap(source, target, Fidelity.Repackage, quality: 60) with { Cost = ResourceCost.Medium };
        }

        foreach (var target in new[] { "zip", "tgz" })
        {
            yield return Cap(CapabilityRegistry.AnyFile, target, Fidelity.Lossless, quality: 70) with
            {
                Action = ActionKind.Compress,
                Options = target == "zip" ? [CompressionOption] : [],
            };
        }

        foreach (var target in new[] { "zip", "tgz", "tar" })
        {
            yield return Cap("folder", target, Fidelity.Lossless, quality: 70) with
            {
                Action = ActionKind.Compress,
                Note = "Links inside the folder are skipped",
                Options = target == "zip" ? [CompressionOption] : [],
                Cost = ResourceCost.Medium,
            };
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var source = context.Input.Format.Id;
        var action = context.Capability.Action;
        if (action == ActionKind.Extract)
        {
            await ExtractAsync(context.Input.Path, source, context.StagingDirectory, context, cancellationToken);
            return;
        }

        if (action == ActionKind.Compress)
        {
            await CreateAsync(context.Input.Path, context.PrimaryOutputPath, context.Target.Id, context, cancellationToken);
            return;
        }

        // Repackage: extract into a private work folder, then archive its contents.
        var work = Path.Combine(context.StagingDirectory, ".work");
        Directory.CreateDirectory(work);
        try
        {
            await ExtractAsync(context.Input.Path, source, work, context, cancellationToken, progressScale: 0.5);
            await CreateFromContentsAsync(work, context.PrimaryOutputPath, context.Target.Id, context, cancellationToken);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private async Task ExtractAsync(string archivePath, string format, string destination, ConversionContext context, CancellationToken cancellationToken, double progressScale = 1)
    {
        var budget = new ExtractionBudget(new FileInfo(archivePath).Length, _limits);
        switch (format)
        {
            case "zip":
                await ExtractZipAsync(archivePath, destination, budget, context, cancellationToken, progressScale);
                break;
            case "tar":
            {
                await using var stream = File.OpenRead(archivePath);
                await ExtractTarAsync(stream, destination, budget, context, cancellationToken);
                break;
            }

            case "tgz":
            {
                await using var stream = File.OpenRead(archivePath);
                await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                await ExtractTarAsync(gzip, destination, budget, context, cancellationToken);
                break;
            }

            case "gz":
            {
                var name = PathSafety.SanitizeFileName(context.Input.BaseName, "decompressed");
                await using var stream = File.OpenRead(archivePath);
                await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                await using var output = new FileStream(Path.Combine(destination, name), FileMode.CreateNew);
                await ArchiveSafety.CopyWithBudgetAsync(gzip, output, budget, cancellationToken);
                break;
            }

            default:
                throw new ConversionException($"The built-in archive engine cannot read {format.ToUpperInvariant()}.");
        }

        context.Report(progressScale, "Extracted");
    }

    private static async Task ExtractZipAsync(string archivePath, string destination, ExtractionBudget budget, ConversionContext context, CancellationToken cancellationToken, double progressScale)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException ex)
        {
            throw new ConversionException("The ZIP file is damaged or not a ZIP archive.", ex.Message, ex);
        }

        using (archive)
        {
            if (archive.Entries.Any(e => e.IsEncrypted))
            {
                throw new ConversionException("This ZIP is password protected. Install 7-Zip to extract encrypted archives.");
            }

            var total = archive.Entries.Count;
            var index = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.AddEntry();
                context.Report(progressScale * index++ / Math.Max(1, total), "Extracting");
                var target = PathSafety.ResolveArchiveEntry(destination, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                // Unix symlinks stored in ZIPs (mode 0120000) are skipped.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    context.AddWarning("Symbolic links in the archive were skipped.");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                await ArchiveSafety.CopyWithBudgetAsync(input, output, budget, cancellationToken);
                output.Close();
                TrySetTime(target, entry.LastWriteTime);
            }
        }
    }

    private static async Task ExtractTarAsync(Stream stream, string destination, ExtractionBudget budget, ConversionContext context, CancellationToken cancellationToken)
    {
        await using var reader = new TarReader(stream);
        TarEntry? entry;
        try
        {
            while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
            {
                budget.AddEntry();
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(PathSafety.ResolveArchiveEntry(destination, entry.Name));
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    {
                        var target = PathSafety.ResolveArchiveEntry(destination, entry.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                        if (entry.DataStream is not null)
                        {
                            await ArchiveSafety.CopyWithBudgetAsync(entry.DataStream, output, budget, cancellationToken);
                        }

                        output.Close();
                        TrySetTime(target, entry.ModificationTime);
                        break;
                    }

                    case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                        context.AddWarning("Links in the archive were skipped for safety.");
                        break;
                    default:
                        // Global/extended headers and special files carry no regular content.
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new ConversionException("The archive is damaged or not a TAR archive.", ex.Message, ex);
        }
    }

    private static async Task CreateAsync(string sourcePath, string outputPath, string format, ConversionContext context, CancellationToken cancellationToken)
    {
        var files = ArchiveSafety.EnumerateForArchive(sourcePath).ToList();
        await WriteArchiveAsync(files, outputPath, format, context, cancellationToken, 0);
    }

    private static async Task CreateFromContentsAsync(string folder, string outputPath, string format, ConversionContext context, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = Directory.EnumerateFiles(folder, "*", options)
            .Select(f => (f, Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/')))
            .ToList();
        await WriteArchiveAsync(files, outputPath, format, context, cancellationToken, 0.5);
    }

    private static async Task WriteArchiveAsync(IReadOnlyList<(string FullPath, string EntryName)> files, string outputPath, string format, ConversionContext context, CancellationToken cancellationToken, double progressStart)
    {
        var level = context.Options.Get("compression", "optimal") switch
        {
            "fastest" => CompressionLevel.Fastest,
            "smallest" => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        if (format == "zip")
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Report(progressStart + (1 - progressStart) * i / Math.Max(1, files.Count), "Compressing");
                var entry = zip.CreateEntry(files[i].EntryName, level);
                entry.LastWriteTime = File.GetLastWriteTime(files[i].FullPath);
                await using var entryStream = entry.Open();
                await using var input = new FileStream(files[i].FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await input.CopyToAsync(entryStream, cancellationToken);
            }

            return;
        }

        Stream tarStream = output;
        GZipStream? gzip = null;
        if (format == "tgz")
        {
            gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
            tarStream = gzip;
        }

        try
        {
            await using (var writer = new TarWriter(tarStream, TarEntryFormat.Pax, leaveOpen: true))
            {
                for (var i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context.Report(progressStart + (1 - progressStart) * i / Math.Max(1, files.Count), "Archiving");
                    await writer.WriteEntryAsync(files[i].FullPath, files[i].EntryName, cancellationToken);
                }
            }
        }
        finally
        {
            if (gzip is not null)
            {
                await gzip.DisposeAsync();
            }
        }
    }

    private static void TrySetTime(string path, DateTimeOffset time)
    {
        try
        {
            if (time.Year > 1980)
            {
                File.SetLastWriteTime(path, time.LocalDateTime);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly OptionDescriptor CompressionOption = OptionDescriptor.Choice("compression", "Compression", "optimal",
        new("fastest", "Fastest"),
        new("optimal", "Balanced"),
        new("smallest", "Smallest"));
}
