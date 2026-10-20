using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Processes;

namespace FileOrbit.Engines.Ghostscript;

/// <summary>
/// PDF optimisation (smaller files) and PDF/A conversion with an installed Ghostscript. Ghostscript
/// is AGPL-licensed and therefore never bundled; FileOrbit only calls a copy the user installed.
/// Ghostscript always runs with -dSAFER, which blocks file-system access from PDF/PostScript content.
/// </summary>
public sealed class GhostscriptEngine : ConversionEngineBase
{
    public const string EngineId = "ghostscript";
    private readonly ExecutableLocator _locator;
    private string? _executable;

    public GhostscriptEngine(ExecutableLocator locator) => _locator = locator;

    public override string Id => EngineId;

    public override string DisplayName => "Ghostscript";

    public override string Description => "Optimises PDFs for size and converts them to PDF/A for archiving, using an installed Ghostscript.";

    public override bool IsExternal => true;

    public override DependencyInfo Dependency { get; } = new(
        "Ghostscript",
        "PDF optimisation (smaller files) and PDF/A archival conversion",
        "GNU AGPL 3.0 (or commercial licence from Artifex)",
        "https://ghostscript.com/releases/gsdnld.html",
        // Ghostscript is not in the winget catalog, so the Engines page links to the official download instead.
        InstallNotes: "Download the 64-bit Windows installer (AGPL release) from the Ghostscript website, run it, then click “Check again”.");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);

    protected override EngineStatus CheckStatus()
    {
        _executable = _locator.Find(Id, "gswin64c.exe",
        [
            @"%ProgramFiles%\gs\gs*\bin\gswin64c.exe",
            @"%ProgramFiles(x86)%\gs\gs*\bin\gswin32c.exe",
        ]);
        if (_executable is null)
        {
            return EngineStatus.Unavailable("Ghostscript is not installed.");
        }

        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(_executable).ProductVersion;
        return EngineStatus.Available("Ready", version, _executable);
    }

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        yield return Cap("pdf", "pdf", Fidelity.Lossy, quality: 70) with
        {
            Action = ActionKind.Transform,
            Variant = "optimized",
            Label = "Optimize",
            Note = "Recompresses images and removes redundant data; text stays selectable",
            Cost = ResourceCost.Medium,
            Options =
            [
                OptionDescriptor.Choice("profile", "Target", "ebook",
                    new("screen", "Smallest (72 DPI images)", "For on-screen viewing only"),
                    new("ebook", "Balanced (150 DPI images)", "Good for sharing by email"),
                    new("printer", "High quality (300 DPI images)", "Suitable for printing")),
            ],
        };
        yield return Cap("pdf", "pdf", Fidelity.Lossless, quality: 70) with
        {
            Action = ActionKind.Transform,
            Variant = "pdfa",
            Label = "PDF/A",
            Note = "Archival PDF/A-2b with embedded fonts. Validate with a PDF/A checker if compliance is legally required",
            Cost = ResourceCost.Medium,
        };
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var executable = (GetStatus().IsAvailable ? _executable : null) ?? throw new ConversionException("Ghostscript is not installed.");
        var arguments = new List<string> { "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET", "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.7" };
        if (context.Capability.Variant == "pdfa")
        {
            arguments.AddRange(["-dPDFA=2", "-dPDFACompatibilityPolicy=1", "-sColorConversionStrategy=RGB", "-dEmbedAllFonts=true"]);
        }
        else
        {
            var profile = context.Options.Get("profile", "ebook");
            arguments.Add($"-dPDFSETTINGS=/{(profile is "screen" or "printer" ? profile : "ebook")}");
            arguments.AddRange(["-dDetectDuplicateImages=true", "-dCompressFonts=true"]);
        }

        if (!string.IsNullOrEmpty(context.Password))
        {
            arguments.Add("-sPDFPassword=" + context.Password);
        }

        arguments.Add("-sOutputFile=" + context.PrimaryOutputPath);
        arguments.Add(context.Input.Path);

        context.Report(null, context.Capability.Variant == "pdfa" ? "Creating PDF/A" : "Optimizing PDF");
        var result = await ProcessRunner.RunAsync(executable, arguments, cancellationToken, timeout: Timeout, log: context.Log);
        if (result.ExitCode != 0)
        {
            var errors = result.StandardError + result.StandardOutput;
            if (errors.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new PasswordRequiredException(wasIncorrect: !string.IsNullOrEmpty(context.Password));
            }

            throw new ConversionException("Ghostscript could not process this PDF.", result.ErrorTail());
        }

        if (context.Capability.Variant == "optimized" && new FileInfo(context.PrimaryOutputPath).Length >= context.Input.SizeBytes)
        {
            context.AddWarning("The optimized file is not smaller than the original; the PDF was already well compressed.");
        }

        context.Report(1, "Done");
    }
}
