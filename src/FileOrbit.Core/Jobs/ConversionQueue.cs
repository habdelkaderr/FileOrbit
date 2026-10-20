namespace FileOrbit.Core.Jobs;

/// <summary>
/// Background queue with bounded concurrency. Jobs never run on the UI thread; observers are
/// notified through events that may fire on any thread.
/// </summary>
public sealed class ConversionQueue : IDisposable
{
    private readonly ConversionRunner _runner;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly List<ConversionJob> _jobs = [];
    private readonly CancellationTokenSource _shutdown = new();

    public ConversionQueue(ConversionRunner runner, int maxConcurrency = 2)
    {
        _runner = runner;
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    public event EventHandler<ConversionJob>? JobAdded;

    public event EventHandler<ConversionJob>? JobRemoved;

    public event EventHandler<ConversionJob>? JobFinished;

    public IReadOnlyList<ConversionJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.ToList();
            }
        }
    }

    public bool HasActiveJobs => Jobs.Any(j => !j.IsFinished);

    public Task Enqueue(ConversionJob job)
    {
        lock (_gate)
        {
            _jobs.Add(job);
        }

        JobAdded?.Invoke(this, job);
        return Task.Run(() => ProcessAsync(job));
    }

    public IReadOnlyList<Task> EnqueueRange(IEnumerable<ConversionJob> jobs) => jobs.Select(Enqueue).ToList();

    public void Cancel(ConversionJob job)
    {
        if (job.CanCancel)
        {
            job.Cancellation.Cancel();
        }
    }

    public void CancelAll()
    {
        foreach (var job in Jobs)
        {
            Cancel(job);
        }
    }

    /// <summary>Re-queues a failed or cancelled job, optionally with a password the user just entered.</summary>
    public Task Retry(ConversionJob job, string? password = null)
    {
        if (!job.CanRetry)
        {
            return Task.CompletedTask;
        }

        job.ResetForRetry();
        job.Password = password;
        return Task.Run(() => ProcessAsync(job));
    }

    public void Remove(ConversionJob job)
    {
        if (!job.IsFinished)
        {
            return;
        }

        bool removed;
        lock (_gate)
        {
            removed = _jobs.Remove(job);
        }

        if (removed)
        {
            JobRemoved?.Invoke(this, job);
        }
    }

    public void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished))
        {
            Remove(job);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        CancelAll();
        _shutdown.Dispose();
    }

    private async Task ProcessAsync(ConversionJob job)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _shutdown.Token);
        try
        {
            await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.Message = "Cancelled before it started.";
            job.CompletedAt = DateTimeOffset.Now;
            JobFinished?.Invoke(this, job);
            return;
        }

        try
        {
            await _runner.RunAsync(job, _shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }

        JobFinished?.Invoke(this, job);
    }
}
