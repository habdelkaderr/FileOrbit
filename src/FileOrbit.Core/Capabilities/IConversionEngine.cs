using FileOrbit.Core.Detection;
using FileOrbit.Core.Formats;
using FileOrbit.Core.Logging;

namespace FileOrbit.Core.Capabilities;

/// <summary>
/// A conversion engine. Engines are independent and replaceable: they declare what they can do,
/// report whether they are usable on this machine, and perform conversions into a staging folder.
/// They never know about the UI and never write directly to the final destination.
/// </summary>
public interface IConversionEngine
{
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>True when the engine needs software that is not part of FileOrbit or Windows.</summary>
    bool IsExternal { get; }

    /// <summary>Information about the external dependency, for the dependency screen.</summary>
    DependencyInfo? Dependency { get; }

    IReadOnlyList<ConversionCapability> Capabilities { get; }

    /// <summary>Checks (and caches) whether the engine can run. Must be cheap after the first call.</summary>
    EngineStatus GetStatus();

    /// <summary>Clears cached availability, e.g. after the user installs a dependency.</summary>
    void Refresh();

    /// <summary>
    /// Availability of one capability. Defaults to the engine's status but engines can refine it,
    /// e.g. an image engine whose HEIC decoder depends on an optional Windows codec.
    /// </summary>
    CapabilityStatus GetCapabilityStatus(ConversionCapability capability) => GetStatus().ToCapabilityStatus();

    /// <summary>
    /// Performs the conversion, writing results only into <see cref="ConversionContext.StagingDirectory"/>.
    /// Throws <see cref="ConversionException"/> for expected failures.
    /// </summary>
    Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken);
}

public sealed record DependencyInfo(
    string Name,
    string Purpose,
    string License,
    string Website,
    string? WingetId = null,
    string? InstallNotes = null);

public sealed record EngineStatus(bool IsAvailable, string Summary, string? Version = null, string? Location = null)
{
    public static EngineStatus Available(string summary = "Ready", string? version = null, string? location = null) =>
        new(true, summary, version, location);

    public static EngineStatus Unavailable(string reason) => new(false, reason);

    public CapabilityStatus ToCapabilityStatus() => new(IsAvailable, IsAvailable ? null : Summary);
}

public sealed record CapabilityStatus(bool IsAvailable, string? Reason)
{
    public static readonly CapabilityStatus Ok = new(true, null);

    public static CapabilityStatus Missing(string reason) => new(false, reason);
}

public sealed record ConversionProgress(double? Fraction, string? Stage = null);

/// <summary>Everything an engine needs to perform one conversion.</summary>
public sealed class ConversionContext
{
    public required DetectedFile Input { get; init; }

    public required FileFormat Target { get; init; }

    public required ConversionCapability Capability { get; init; }

    public required ConversionOptions Options { get; init; }

    /// <summary>Empty, private folder on the destination volume. All outputs go here.</summary>
    public required string StagingDirectory { get; init; }

    /// <summary>Base name to use for outputs (without extension).</summary>
    public required string OutputBaseName { get; init; }

    /// <summary>Password supplied deliberately by the user for a protected input, if any.</summary>
    public string? Password { get; init; }

    public IProgress<ConversionProgress> Progress { get; init; } = new Progress<ConversionProgress>();

    public IAppLog Log { get; init; } = NullLog.Instance;

    /// <summary>Non-fatal caveats to show the user (e.g. "Only the first frame was converted").</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings)
            {
                return _warnings.ToList();
            }
        }
    }

    private readonly List<string> _warnings = [];

    public void AddWarning(string warning)
    {
        lock (_warnings)
        {
            if (!_warnings.Contains(warning))
            {
                _warnings.Add(warning);
            }
        }
    }

    /// <summary>Path for the primary output file inside the staging folder.</summary>
    public string PrimaryOutputPath => Path.Combine(StagingDirectory, OutputBaseName + Target.PrimaryExtension);

    /// <summary>Path for an additional output (e.g. page 2) inside the staging folder.</summary>
    public string OutputPath(string fileName) => Path.Combine(StagingDirectory, fileName);

    public void Report(double? fraction, string? stage = null) =>
        Progress.Report(new ConversionProgress(fraction is null ? null : Math.Clamp(fraction.Value, 0, 1), stage));
}

/// <summary>An expected conversion failure with a message suitable for the user.</summary>
public class ConversionException : Exception
{
    public ConversionException(string userMessage, string? diagnostic = null, Exception? inner = null)
        : base(userMessage, inner)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Technical details for the log (exit code, stderr tail, ...).</summary>
    public string? Diagnostic { get; }
}

/// <summary>The input is encrypted and needs a password the user has not supplied (or supplied incorrectly).</summary>
public sealed class PasswordRequiredException : ConversionException
{
    public PasswordRequiredException(bool wasIncorrect)
        : base(wasIncorrect ? "The password is incorrect." : "This file is password protected.")
    {
        WasIncorrect = wasIncorrect;
    }

    public bool WasIncorrect { get; }
}
