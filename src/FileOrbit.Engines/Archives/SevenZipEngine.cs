using System.Globalization;
using System.Text.RegularExpressions;
using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Processes;

namespace FileOrbit.Engines.Archives;

/// <summary>
/// Uses an installed 7-Zip (7z.exe) for 7Z and RAR archives and for password-protected ZIPs.
/// 7-Zip is not bundled; FileOrbit only uses it when the user has installed it.
/// </summary>
public sealed partial class SevenZipEngine : ConversionEngineBase
{
    public const string EngineId = "7zip";
    private readonly ExecutableLocator _locator;
    private string? _executable;

    public SevenZipEngine(ExecutableLocator locator) => _locator = locator;

    public override string Id => EngineId;

    public override string DisplayName => "7-Zip";

    public override string Description => "Extracts 7Z, RAR and encrypted ZIP archives and creates 7Z archives using the installed 7-Zip.";

    public override bool IsExternal => true;

    public override DependencyInfo Dependency { get; } = new(
        "7-Zip",
        "7Z and RAR archives, password-protected ZIP files",
        "GNU LGPL (with unRAR restriction)",
        "https://www.7-zip.org/",
        WingetId: "7zip.7zip");

    protected override EngineStatus CheckStatus()
    {
        _executable = _locator.Find(Id, "7z.exe",
        [
            @"%ProgramFiles%\7-Zip\7z.exe",
            @"%ProgramFiles(x86)%\7-Zip\7z.exe",
            @"%LOCALAPPDATA%\Programs\7-Zip\7z.exe",
        ]);
        if (_executable is null)
        {
            return EngineStatus.Unavailable("7-Zip is not installed.");
        }

        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(_executable).ProductVersion;
        return EngineStatus.Available("Ready", version, _executable);
    }

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var source in new[] { "7z", "rar", "zip", "tar", "tgz", "gz" })
        {
            yield return Cap(source, "folder", Fidelity.Repackage, quality: source is "7z" or "rar" ? 80 : 40) with
            {
                Action = ActionKind.Extract,
                Shape = OutputShape.Folder,
                Note = "Extracts into a new folder; supports password-protected archives",
                Cost = ResourceCost.Medium,
            };
        }

        foreach (var (source, target) in new[] { ("7z", "zip"), ("rar", "zip"), ("rar", "7z"), ("zip", "7z"), ("tgz", "7z"), ("tar", "7z") })
        {
            yield return Cap(source, target, Fidelity.Repackage, quality: 60) with { Cost = ResourceCost.Medium, Options = target == "7z" ? [LevelOption] : [] };
        }

        yield return Cap(CapabilityRegistry.AnyFile, "7z", Fidelity.Lossless, quality: 80) with { Action = ActionKind.Compress, Options = [LevelOption] };
        yield return Cap("folder", "7z", Fidelity.Lossless, quality: 80) with { Action = ActionKind.Compress, Options = [LevelOption], Cost = ResourceCost.Medium };
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var executable = (GetStatus().IsAvailable ? _executable : null) ?? throw new ConversionException("7-Zip is not installed.");
        switch (context.Capability.Action)
        {
            case ActionKind.Extract:
                await ExtractAsync(executable, context.Input.Path, context.StagingDirectory, context, cancellationToken, 1);
                break;
            case ActionKind.Compress:
                await CompressAsync(executable, [context.Input.Path], context.PrimaryOutputPath, context, cancellationToken, 0);
                break;
            default:
            {
                var work = Path.Combine(context.StagingDirectory, ".work");
                Directory.CreateDirectory(work);
                try
                {
                    await ExtractAsync(executable, context.Input.Path, work, context, cancellationToken, 0.5);
                    var entries = Directory.GetFileSystemEntries(work);
                    await CompressAsync(executable, entries, context.PrimaryOutputPath, context, cancellationToken, 0.5);
                }
                finally
                {
                    Directory.Delete(work, recursive: true);
                }

                break;
            }
        }
    }

    private static async Task ExtractAsync(string executable, string archive, string destination, ConversionContext context, CancellationToken cancellationToken, double scale)
    {
        var arguments = new List<string>
        {
            "x",
            "-y",
            "-bb0",
            "-bsp1",
            "-bso0",
            "-o" + destination,
            // Always pass a password switch so 7-Zip never waits for interactive input.
            "-p" + (context.Password ?? string.Empty),
            "--",
            archive,
        };
        var result = await ProcessRunner.RunAsync(executable, arguments, cancellationToken,
            onStandardOutputLine: line => ReportProgress(line, context, 0, scale, "Extracting"),
            log: context.Log);
        ThrowOnFailure(result, context);

        var removed = ArchiveSafety.RemoveReparsePoints(destination);
        if (removed > 0)
        {
            context.AddWarning($"{removed} link(s) in the archive were removed for safety.");
        }
    }

    private static async Task CompressAsync(string executable, IReadOnlyList<string> sources, string output, ConversionContext context, CancellationToken cancellationToken, double start)
    {
        var type = context.Target.Id == "zip" ? "zip" : "7z";
        var level = context.Options.Get("level", "5");
        var arguments = new List<string> { "a", "-t" + type, "-mx=" + level, "-bb0", "-bsp1", "-bso0", "-snl", "--", output };
        arguments.AddRange(sources);
        var result = await ProcessRunner.RunAsync(executable, arguments, cancellationToken,
            onStandardOutputLine: line => ReportProgress(line, context, start, 1 - start, "Compressing"),
            log: context.Log);
        ThrowOnFailure(result, context);
    }

    private static void ReportProgress(string line, ConversionContext context, double start, double scale, string stage)
    {
        var match = PercentPattern().Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
        {
            context.Report(start + scale * percent / 100.0, stage);
        }
    }

    private static void ThrowOnFailure(ProcessResult result, ConversionContext context)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var errors = result.StandardError + result.StandardOutput;
        if (errors.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) || errors.Contains("Can not open encrypted archive", StringComparison.OrdinalIgnoreCase))
        {
            throw new PasswordRequiredException(wasIncorrect: !string.IsNullOrEmpty(context.Password));
        }

        if (result.ExitCode == 1)
        {
            context.AddWarning("7-Zip reported warnings; some files may have been skipped.");
            return;
        }

        var message = result.ExitCode switch
        {
            2 => "7-Zip could not process the archive. It may be damaged or in an unsupported format.",
            8 => "7-Zip ran out of memory.",
            _ => $"7-Zip failed (exit code {result.ExitCode}).",
        };
        throw new ConversionException(message, result.ErrorTail());
    }

    [GeneratedRegex(@"^\s*(\d{1,3})%")]
    private static partial Regex PercentPattern();

    private static readonly OptionDescriptor LevelOption = OptionDescriptor.Choice("level", "Compression", "5",
        new("1", "Fastest"),
        new("5", "Normal"),
        new("9", "Ultra", "Smallest files, slowest"));
}
