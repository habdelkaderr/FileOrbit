using System.ComponentModel;
using System.Runtime.CompilerServices;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Planning;

namespace FileOrbit.Core.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
}

/// <summary>One file being converted. Observable so the UI can bind to it directly.</summary>
public sealed class ConversionJob : INotifyPropertyChanged
{
    private JobStatus _status = JobStatus.Queued;
    private double? _progress;
    private string? _stage;
    private string? _message;
    private IReadOnlyList<string> _outputs = [];
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private bool _needsPassword;
    private IReadOnlyList<string> _warnings = [];

    public ConversionJob(DetectedFile input, ConversionAction action, EngineCandidate engine, ConversionOptions options, string outputDirectory, string baseName, CollisionPolicy collisionPolicy)
    {
        Input = input;
        Action = action;
        Engine = engine;
        Options = options;
        OutputDirectory = outputDirectory;
        BaseName = baseName;
        CollisionPolicy = collisionPolicy;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();

    public DetectedFile Input { get; }

    public ConversionAction Action { get; }

    public EngineCandidate Engine { get; }

    public ConversionOptions Options { get; }

    public string OutputDirectory { get; }

    public string BaseName { get; }

    public CollisionPolicy CollisionPolicy { get; }

    /// <summary>Password for protected inputs. Cleared as soon as the job finishes.</summary>
    public string? Password { get; set; }

    internal CancellationTokenSource Cancellation { get; private set; } = new();

    public JobStatus Status
    {
        get => _status;
        internal set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(IsFinished));
                Raise(nameof(CanRetry));
                Raise(nameof(CanCancel));
            }
        }
    }

    /// <summary>0..1 when the engine reports progress, null for indeterminate.</summary>
    public double? Progress { get => _progress; internal set => Set(ref _progress, value); }

    public string? Stage { get => _stage; internal set => Set(ref _stage, value); }

    /// <summary>User-facing error or result message.</summary>
    public string? Message { get => _message; internal set => Set(ref _message, value); }

    public string? Diagnostic { get; internal set; }

    public bool NeedsPassword { get => _needsPassword; internal set => Set(ref _needsPassword, value); }

    public IReadOnlyList<string> Outputs
    {
        get => _outputs;
        internal set
        {
            if (Set(ref _outputs, value))
            {
                Raise(nameof(PrimaryOutput));
            }
        }
    }

    /// <summary>Non-fatal caveats reported by the engine.</summary>
    public IReadOnlyList<string> Warnings { get => _warnings; internal set => Set(ref _warnings, value); }

    public string? PrimaryOutput => _outputs.Count > 0 ? _outputs[0] : null;

    public DateTimeOffset? StartedAt { get => _startedAt; internal set => Set(ref _startedAt, value); }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        internal set
        {
            if (Set(ref _completedAt, value))
            {
                Raise(nameof(Duration));
            }
        }
    }

    public TimeSpan? Duration => _startedAt is { } start && _completedAt is { } end ? end - start : null;

    public bool IsFinished => _status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped;

    public bool CanRetry => _status is JobStatus.Failed or JobStatus.Cancelled;

    public bool CanCancel => _status is JobStatus.Queued or JobStatus.Running;

    public string Title => $"{Input.FileName} → {Action.Verb}";

    internal void ResetForRetry()
    {
        Cancellation.Dispose();
        Cancellation = new CancellationTokenSource();
        Status = JobStatus.Queued;
        Progress = null;
        Stage = null;
        Message = null;
        Diagnostic = null;
        NeedsPassword = false;
        Outputs = [];
        Warnings = [];
        StartedAt = null;
        CompletedAt = null;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
