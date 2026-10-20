using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Processes;

namespace FileOrbit.App.AddOns;

/// <summary>
/// Stands in for the optional Ghostscript engine plugin while Ghostscript is not installed. The plugin
/// itself is not loaded in that case (so Windows never has to evaluate it), but users still see the
/// add-on on the Engines page with setup steps, and the PDF actions it would add appear dimmed.
/// </summary>
internal sealed class GhostscriptPlaceholder : ConversionEngineBase
{
    public override string Id => KnownTools.GhostscriptEngineId;

    public override string DisplayName => "Ghostscript";

    public override string Description => "Optimises PDFs for size and converts them to PDF/A for archiving, using an installed Ghostscript.";

    public override bool IsExternal => true;

    public override DependencyInfo Dependency => KnownTools.Ghostscript;

    protected override EngineStatus CheckStatus() => EngineStatus.Unavailable("Ghostscript is not installed.");

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        yield return Cap("pdf", "pdf", Fidelity.Lossy) with { Action = ActionKind.Transform, Variant = "optimized", Label = "Optimize" };
        yield return Cap("pdf", "pdf", Fidelity.Lossless) with { Action = ActionKind.Transform, Variant = "pdfa", Label = "PDF/A" };
    }

    public override Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken) =>
        throw new ConversionException("Ghostscript is not installed.");
}
