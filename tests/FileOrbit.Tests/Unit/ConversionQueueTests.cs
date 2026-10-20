using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Jobs;
using FileOrbit.Core.Planning;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Unit;

public sealed class ConversionQueueTests
{
    private static (Pipeline Pipeline, FakeEngine Engine) Setup(Func<ConversionContext, CancellationToken, Task>? behavior = null)
    {
        var engine = new FakeEngine("fake").Supports("csv", "json");
        if (behavior is not null)
        {
            engine.Behavior = behavior;
        }

        return (new Pipeline([engine]), engine);
    }

    private static string Json(TestDirectory dir, string name = "data.csv") => dir.Write(name, "a,b\n1,2\n");

    private static Func<ConversionContext, CancellationToken, Task> WriteJson =>
        (context, token) => File.WriteAllTextAsync(context.PrimaryOutputPath, "{\"ok\":true}", token);

    [Fact]
    public async Task Successful_job_moves_output_into_place_and_preserves_source()
    {
        var (pipeline, _) = Setup(WriteJson);
        using var dir = new TestDirectory();
        var input = Json(dir);

        var job = pipeline.CreateJob(input, "convert:json");
        await pipeline.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(dir.File("data.json"), job.PrimaryOutput);
        Assert.Equal("a,b\n1,2\n", await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
        pipeline.AssertNoStagingLeft(dir.Path);
    }

    [Fact]
    public async Task Renamed_or_fake_output_is_rejected_by_validation()
    {
        // An engine that "succeeds" but writes something that is not JSON must not be reported as success.
        var (pipeline, _) = Setup((context, token) => File.WriteAllTextAsync(context.PrimaryOutputPath, "not json at all {", token));
        using var dir = new TestDirectory();

        var job = pipeline.CreateJob(Json(dir), "convert:json");
        await pipeline.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("invalid JSON", job.Message);
        Assert.False(File.Exists(dir.File("data.json")));
        pipeline.AssertNoStagingLeft(dir.Path);
    }

    [Fact]
    public async Task Engine_that_produces_nothing_fails()
    {
        var (pipeline, _) = Setup((_, _) => Task.CompletedTask);
        using var dir = new TestDirectory();

        var job = pipeline.CreateJob(Json(dir), "convert:json");
        await pipeline.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("no output", job.Message);
    }

    [Fact]
    public async Task Engine_failure_is_reported_with_diagnostics()
    {
        var (pipeline, _) = Setup((_, _) => throw new ConversionException("The tool crashed.", "exit code 3"));
        using var dir = new TestDirectory();

        var job = pipeline.CreateJob(Json(dir), "convert:json");
        await pipeline.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("The tool crashed.", job.Message);
        Assert.Equal("exit code 3", job.Diagnostic);
        Assert.True(job.CanRetry);
    }

    [Fact]
    public async Task Cancellation_stops_the_job_and_cleans_staging()
    {
        var started = new TaskCompletionSource();
        var (pipeline, _) = Setup(async (context, token) =>
        {
            await File.WriteAllTextAsync(context.PrimaryOutputPath, "{\"partial\":", token);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        using var dir = new TestDirectory();
        using var queue = new ConversionQueue(pipeline.Runner);

        var job = pipeline.CreateJob(Json(dir), "convert:json");
        var run = queue.Enqueue(job);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        queue.Cancel(job);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.False(File.Exists(dir.File("data.json")));
        pipeline.AssertNoStagingLeft(dir.Path);
    }

    [Fact]
    public async Task Queued_job_can_be_cancelled_before_it_starts()
    {
        var gate = new TaskCompletionSource();
        var (pipeline, _) = Setup(async (context, token) =>
        {
            await gate.Task.WaitAsync(token);
            await File.WriteAllTextAsync(context.PrimaryOutputPath, "{}", token);
        });
        using var dir = new TestDirectory();
        using var queue = new ConversionQueue(pipeline.Runner, maxConcurrency: 1);

        var first = pipeline.CreateJob(Json(dir, "a.csv"), "convert:json");
        var second = pipeline.CreateJob(Json(dir, "b.csv"), "convert:json");
        var runs = queue.EnqueueRange([first, second]);
        queue.Cancel(second);
        gate.SetResult();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Succeeded, first.Status);
        Assert.Equal(JobStatus.Cancelled, second.Status);
    }

    [Fact]
    public async Task Batch_respects_concurrency_limit_and_converts_every_file()
    {
        var running = 0;
        var peak = 0;
        var (pipeline, _) = Setup(async (context, token) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(50, token);
            await File.WriteAllTextAsync(context.PrimaryOutputPath, "[]", token);
            Interlocked.Decrement(ref running);
        });
        using var dir = new TestDirectory();
        using var queue = new ConversionQueue(pipeline.Runner, maxConcurrency: 2);
        var finished = 0;
        queue.JobFinished += (_, _) => Interlocked.Increment(ref finished);

        var jobs = Enumerable.Range(1, 6).Select(i => pipeline.CreateJob(Json(dir, $"file{i}.csv"), "convert:json")).ToList();
        await Task.WhenAll(queue.EnqueueRange(jobs)).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.All(jobs, j => Assert.Equal(JobStatus.Succeeded, j.Status));
        Assert.Equal(6, Directory.GetFiles(dir.Path, "*.json").Length);
        Assert.InRange(peak, 1, 2);
        Assert.Equal(6, finished);
    }

    [Fact]
    public async Task Failed_job_can_be_retried_successfully()
    {
        var attempts = 0;
        var (pipeline, _) = Setup((context, token) =>
        {
            if (++attempts == 1)
            {
                throw new ConversionException("Temporary failure.");
            }

            return File.WriteAllTextAsync(context.PrimaryOutputPath, "{}", token);
        });
        using var dir = new TestDirectory();
        using var queue = new ConversionQueue(pipeline.Runner);

        var job = pipeline.CreateJob(Json(dir), "convert:json");
        await queue.Enqueue(job);
        Assert.Equal(JobStatus.Failed, job.Status);

        await queue.Retry(job);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Null(job.Message is "Temporary failure." ? job.Message : null);
    }

    [Fact]
    public async Task Password_is_requested_then_used_once_and_cleared()
    {
        string? seen = null;
        var (pipeline, _) = Setup((context, token) =>
        {
            seen = context.Password;
            if (context.Password != "s3cret")
            {
                throw new PasswordRequiredException(wasIncorrect: context.Password is not null);
            }

            return File.WriteAllTextAsync(context.PrimaryOutputPath, "{}", token);
        });
        using var dir = new TestDirectory();
        using var queue = new ConversionQueue(pipeline.Runner);

        var job = pipeline.CreateJob(Json(dir), "convert:json");
        await queue.Enqueue(job);
        Assert.True(job.NeedsPassword);
        Assert.Equal(JobStatus.Failed, job.Status);

        await queue.Retry(job, "s3cret");

        Assert.Equal("s3cret", seen);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Null(job.Password);
    }

    [Fact]
    public async Task Existing_output_is_kept_unless_replace_is_chosen()
    {
        var (pipeline, _) = Setup(WriteJson);
        using var dir = new TestDirectory();
        var input = Json(dir);
        dir.Write("data.json", "\"original\"");

        var keepBoth = pipeline.CreateJob(input, "convert:json");
        await pipeline.Runner.RunAsync(keepBoth, TestContext.Current.CancellationToken);
        Assert.Equal(dir.File("data (1).json"), keepBoth.PrimaryOutput);
        Assert.Equal("\"original\"", await File.ReadAllTextAsync(dir.File("data.json"), TestContext.Current.CancellationToken));

        var skip = pipeline.CreateJob(input, "convert:json", policy: CollisionPolicy.Skip);
        await pipeline.Runner.RunAsync(skip, TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.Skipped, skip.Status);

        var replace = pipeline.CreateJob(input, "convert:json", policy: CollisionPolicy.Replace);
        await pipeline.Runner.RunAsync(replace, TestContext.Current.CancellationToken);
        Assert.Equal(dir.File("data.json"), replace.PrimaryOutput);
        Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(dir.File("data.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Source_deleted_before_start_fails_cleanly()
    {
        var (pipeline, engine) = Setup(WriteJson);
        using var dir = new TestDirectory();
        var input = Json(dir);
        var job = pipeline.CreateJob(input, "convert:json");
        File.Delete(input);

        await pipeline.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Multi_file_output_is_delivered_as_a_folder()
    {
        var engine = new FakeEngine("pages").Supports("csv", "json", shape: OutputShape.FileOrFolder);
        engine.Behavior = async (context, token) =>
        {
            await File.WriteAllTextAsync(context.OutputPath("data - 1.json"), "{}", token);
            await File.WriteAllTextAsync(context.OutputPath("data - 2.json"), "{}", token);
        };
        var pipeline = new Pipeline([engine]);
        using var dir = new TestDirectory();

        var job = pipeline.CreateJob(Json(dir), "convert:json");
        await pipeline.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(dir.File("data"), job.PrimaryOutput);
        Assert.Equal(2, Directory.GetFiles(job.PrimaryOutput!).Length);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
