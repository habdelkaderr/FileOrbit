using FileOrbit.Core.Formats;

namespace FileOrbit.Core.Capabilities;

/// <summary>A candidate engine for an action together with its availability on this machine.</summary>
public sealed record EngineCandidate(IConversionEngine Engine, ConversionCapability Capability, CapabilityStatus Status);

/// <summary>
/// A user-facing action for a source format ("Convert to PNG", "Extract", "Resize") backed by one or
/// more engines. The best available engine is used unless the user picks another.
/// </summary>
public sealed class ConversionAction
{
    public ConversionAction(FileFormat source, FileFormat target, ActionKind kind, string? variant, IReadOnlyList<EngineCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException("An action needs at least one candidate engine.", nameof(candidates));
        }

        Source = source;
        Target = target;
        Kind = kind;
        Variant = variant;
        Candidates = candidates;
    }

    public FileFormat Source { get; }

    public FileFormat Target { get; }

    public ActionKind Kind { get; }

    public string? Variant { get; }

    /// <summary>Ordered best first: available engines before unavailable ones, then by quality.</summary>
    public IReadOnlyList<EngineCandidate> Candidates { get; }

    public string Key => MakeKey(Kind, Target.Id, Variant);

    public bool IsAvailable => Candidates[0].Status.IsAvailable;

    public EngineCandidate Best => Candidates[0];

    public IEnumerable<EngineCandidate> AvailableCandidates => Candidates.Where(c => c.Status.IsAvailable);

    /// <summary>Why the action cannot run, when no engine is available.</summary>
    public string? UnavailableReason => IsAvailable ? null : Best.Status.Reason;

    /// <summary>The dependency the user could install to enable this action, if any.</summary>
    public DependencyInfo? MissingDependency => IsAvailable ? null : Candidates.Select(c => c.Engine.Dependency).FirstOrDefault(d => d is not null);

    public string Title => Best.Capability.Label ?? Kind switch
    {
        ActionKind.Extract => "Extract",
        ActionKind.Transform => Variant ?? Target.Name,
        _ => Target.Name,
    };

    public string Verb => Kind switch
    {
        ActionKind.Extract => "Extract archive",
        ActionKind.Compress => $"Compress to {Target.Name}",
        ActionKind.Transform => Best.Capability.Label ?? "Transform",
        _ => $"Convert to {Target.Name}",
    };

    public string Description
    {
        get
        {
            var capability = Best.Capability;
            var parts = new List<string>
            {
                Kind == ActionKind.Extract ? "Unpack into a new folder" : Target.FullName,
                FidelityText(capability.Fidelity),
            };
            if (capability.Note is not null)
            {
                parts.Add(capability.Note);
            }

            return string.Join(". ", parts.Where(p => !string.IsNullOrEmpty(p))) + ".";
        }
    }

    public FormatCategory Category => Kind switch
    {
        ActionKind.Extract or ActionKind.Compress => FormatCategory.Archive,
        ActionKind.Transform => FormatCategory.Other,
        _ => Target.Category,
    };

    public static string MakeKey(ActionKind kind, string targetId, string? variant) =>
        variant is null ? $"{kind}:{targetId}".ToLowerInvariant() : $"{kind}:{targetId}:{variant}".ToLowerInvariant();

    public static string FidelityText(Fidelity fidelity) => fidelity switch
    {
        Fidelity.Lossless => "Lossless",
        Fidelity.Lossy => "Lossy: some quality is traded for size",
        Fidelity.Extraction => "Extracts content only; layout and formatting are not preserved",
        Fidelity.LayoutSensitive => "Layout is recreated; complex documents may not convert perfectly",
        Fidelity.Rasterized => "Rendered as pictures; text will not be editable",
        Fidelity.Repackage => "Contents are repackaged unchanged",
        _ => string.Empty,
    };

    public override string ToString() => $"{Source.Id} -> {Key} ({Best.Engine.Id})";
}
