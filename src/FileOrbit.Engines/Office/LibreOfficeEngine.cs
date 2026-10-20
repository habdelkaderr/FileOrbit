using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Processes;

namespace FileOrbit.Engines.Office;

/// <summary>
/// Uses an installed LibreOffice in headless mode. Each conversion runs with its own throw-away
/// user profile, so it never interferes with an open LibreOffice window and always starts with
/// default (high) macro security.
/// </summary>
public sealed class LibreOfficeEngine : ConversionEngineBase
{
    public const string EngineId = "libreoffice";

    private static readonly string[] WriterSources = ["docx", "doc", "odt", "rtf", "txt", "html"];
    private static readonly string[] WriterTargets = ["pdf", "docx", "odt", "rtf", "html", "txt"];
    private static readonly string[] CalcSources = ["xlsx", "xls", "ods", "csv"];
    private static readonly string[] CalcTargets = ["pdf", "xlsx", "ods", "csv", "html"];
    private static readonly string[] ImpressSources = ["pptx", "ppt", "odp"];
    private static readonly string[] ImpressTargets = ["pdf", "pptx", "odp"];

    private readonly ExecutableLocator _locator;
    private string? _executable;

    public LibreOfficeEngine(ExecutableLocator locator) => _locator = locator;

    public override string Id => EngineId;

    public override string DisplayName => "LibreOffice";

    public override string Description => "Converts documents, spreadsheets and presentations using an installed LibreOffice (headless).";

    public override bool IsExternal => true;

    public override DependencyInfo Dependency { get; } = new(
        "LibreOffice",
        "Free document, spreadsheet and presentation conversion, including OpenDocument formats",
        "Mozilla Public License 2.0",
        "https://www.libreoffice.org/download/",
        WingetId: "TheDocumentFoundation.LibreOffice");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    protected override EngineStatus CheckStatus()
    {
        _executable = _locator.Find(Id, "soffice.exe",
        [
            @"%ProgramFiles%\LibreOffice\program\soffice.exe",
            @"%ProgramFiles(x86)%\LibreOffice\program\soffice.exe",
        ]);
        if (_executable is null)
        {
            return EngineStatus.Unavailable("LibreOffice is not installed.");
        }

        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(_executable).ProductVersion;
        return EngineStatus.Available("Ready", version, Path.GetDirectoryName(_executable));
    }

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var (sources, targets) in new[] { (WriterSources, WriterTargets), (CalcSources, CalcTargets), (ImpressSources, ImpressTargets) })
        {
            foreach (var source in sources)
            {
                foreach (var target in targets.Where(t => t != source))
                {
                    var isOpenDocument = source is "odt" or "ods" or "odp" || target is "odt" or "ods" or "odp";
                    yield return Cap(source, target, target switch
                    {
                        "pdf" or "html" => Fidelity.LayoutSensitive,
                        "txt" or "csv" => Fidelity.Extraction,
                        _ => Fidelity.LayoutSensitive,
                    }, quality: isOpenDocument ? 85 : source is "txt" or "html" or "csv" ? 65 : 70) with
                    {
                        Note = target == "csv" ? "Exports the first sheet only" : isOpenDocument ? null : "Formatting may differ slightly from Microsoft Office",
                        Cost = ResourceCost.Medium,
                    };
                }
            }
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var executable = (GetStatus().IsAvailable ? _executable : null) ?? throw new ConversionException("LibreOffice is not installed.");
        var profile = Path.Combine(context.StagingDirectory, ".profile");
        var outDir = Path.Combine(context.StagingDirectory, ".out");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(outDir);
        try
        {
            var arguments = new List<string>
            {
                "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri,
                "--headless",
                "--invisible",
                "--norestore",
                "--nologo",
                "--nodefault",
                "--nolockcheck",
                "--convert-to",
                FilterFor(context.Input.Format.Id, context.Target.Id),
                "--outdir",
                outDir,
                context.Input.Path,
            };

            context.Report(null, "Converting with LibreOffice");
            var result = await ProcessRunner.RunAsync(executable, arguments, cancellationToken, timeout: Timeout, log: context.Log);
            var produced = Directory.GetFiles(outDir);
            if (result.ExitCode != 0 || produced.Length == 0)
            {
                var detail = $"exit {result.ExitCode}: {result.ErrorTail()} {result.StandardOutput}";
                throw new ConversionException(
                    detail.Contains("password", StringComparison.OrdinalIgnoreCase)
                        ? "The file is password protected; LibreOffice cannot convert it without the password."
                        : "LibreOffice could not convert the file.",
                    detail);
            }

            File.Move(produced[0], context.PrimaryOutputPath);
        }
        finally
        {
            TryDelete(profile);
            TryDelete(outDir);
        }

        context.Report(1, "Done");
    }

    internal static string FilterFor(string source, string target) => (source, target) switch
    {
        (_, "pdf") => "pdf",
        (_, "docx") => "docx:MS Word 2007 XML",
        (_, "xlsx") => "xlsx:Calc MS Excel 2007 XML",
        (_, "pptx") => "pptx:Impress MS PowerPoint 2007 XML",
        (_, "txt") => "txt:Text (encoded):UTF8",
        // Comma separator, double-quote text delimiter, UTF-8 (76), first line not special.
        (_, "csv") => "csv:Text - txt - csv (StarCalc):44,34,76,1",
        (_, "html") when source is "xlsx" or "xls" or "ods" or "csv" => "html:HTML (StarCalc)",
        (_, "html") => "html:HTML (StarWriter)",
        _ => target,
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
