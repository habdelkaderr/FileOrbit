namespace FileOrbit.Core.Capabilities;

/// <summary>What kind of operation the action performs, independent of formats.</summary>
public enum ActionKind
{
    /// <summary>Produce a new file in another format.</summary>
    Convert,

    /// <summary>Unpack an archive into a folder.</summary>
    Extract,

    /// <summary>Pack a file or folder into an archive.</summary>
    Compress,

    /// <summary>Produce a modified copy in the same format (resize, re-compress, optimise).</summary>
    Transform,
}

/// <summary>How faithfully the output represents the input.</summary>
public enum Fidelity
{
    /// <summary>No information is lost.</summary>
    Lossless,

    /// <summary>Quality is traded for size (e.g. JPEG, MP3).</summary>
    Lossy,

    /// <summary>Only text or tabular data is extracted; layout and formatting are dropped.</summary>
    Extraction,

    /// <summary>The document is re-laid out by an engine; complex layouts may shift.</summary>
    LayoutSensitive,

    /// <summary>Pages or slides are rendered to pictures; the result is not editable.</summary>
    Rasterized,

    /// <summary>Content is moved between containers unchanged (archives).</summary>
    Repackage,
}

/// <summary>Shape of what a conversion produces.</summary>
public enum OutputShape
{
    /// <summary>Exactly one output file.</summary>
    File,

    /// <summary>One file, or a folder of files when the input has several pages, slides or sheets.</summary>
    FileOrFolder,

    /// <summary>Always a folder (e.g. archive extraction).</summary>
    Folder,
}

public enum ResourceCost
{
    Low,
    Medium,
    High,
}

/// <summary>
/// A single source→target operation that one engine can perform. Engines declare these statically;
/// the <see cref="CapabilityRegistry"/> combines them with runtime availability.
/// </summary>
public sealed record ConversionCapability
{
    public required string EngineId { get; init; }

    public required string SourceFormatId { get; init; }

    public required string TargetFormatId { get; init; }

    public ActionKind Action { get; init; } = ActionKind.Convert;

    /// <summary>Optional distinguishing variant for multiple actions with the same target (e.g. "resize").</summary>
    public string? Variant { get; init; }

    /// <summary>Optional label overriding the target format name (e.g. "Resize").</summary>
    public string? Label { get; init; }

    public Fidelity Fidelity { get; init; } = Fidelity.Lossless;

    /// <summary>Short user-facing caveat, e.g. "Complex layouts may not convert perfectly."</summary>
    public string? Note { get; init; }

    /// <summary>Engine preference when several engines can perform the same action (higher is better).</summary>
    public int Quality { get; init; } = 50;

    public OutputShape Shape { get; init; } = OutputShape.File;

    public ResourceCost Cost { get; init; } = ResourceCost.Low;

    /// <summary>False when the engine cannot be interrupted part-way (e.g. a single Office automation call).</summary>
    public bool SupportsCancellation { get; init; } = true;

    public IReadOnlyList<OptionDescriptor> Options { get; init; } = [];

    /// <summary>Identifies the user-facing action independent of which engine performs it.</summary>
    public string ActionKey => ConversionAction.MakeKey(Action, TargetFormatId, Variant);

    public string Id => $"{EngineId}:{SourceFormatId}:{ActionKey}";
}
