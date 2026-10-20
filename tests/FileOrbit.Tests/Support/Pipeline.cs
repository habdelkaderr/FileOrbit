using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Formats;
using FileOrbit.Core.Jobs;
using FileOrbit.Core.Logging;
using FileOrbit.Core.Planning;
using FileOrbit.Engines;

namespace FileOrbit.Tests.Support;

/// <summary>The real conversion pipeline (detector → registry → runner) for integration tests.</summary>
public sealed class Pipeline
{
    public Pipeline(IEnumerable<IConversionEngine>? engines = null)
    {
        Formats = FormatCatalog.CreateDefault();
        Detector = new FileTypeDetector(Formats);
        Registry = new CapabilityRegistry(Formats, engines ?? EngineCatalog.CreateDefault());
        Runner = new ConversionRunner(new OutputValidator(Detector, EngineCatalog.CreateOutputChecks()), NullLog.Instance);
    }

    public static Pipeline Real { get; } = new();

    public FormatCatalog Formats { get; }

    public FileTypeDetector Detector { get; }

    public CapabilityRegistry Registry { get; }

    public ConversionRunner Runner { get; }

    public ConversionJob CreateJob(string inputPath, string actionKey, IReadOnlyDictionary<string, string>? options = null, string? outputDirectory = null,
        string? engineId = null, CollisionPolicy policy = CollisionPolicy.KeepBoth, string? password = null)
    {
        var input = Detector.Detect(inputPath);
        var action = Registry.Resolve(input.Format, actionKey)
                     ?? throw new InvalidOperationException($"No action {actionKey} for {input.Format.Id}. Available: {string.Join(", ", Registry.GetActions(input.Format).Select(a => a.Key))}");
        var candidate = engineId is null ? action.Best : action.Candidates.Single(c => c.Engine.Id == engineId);
        var directory = outputDirectory ?? Path.GetDirectoryName(inputPath)!;
        var job = new ConversionJob(input, action, candidate, ConversionOptions.For(candidate.Capability, options), directory, OutputPlanner.DefaultBaseName(input, action), policy)
        {
            Password = password,
        };
        return job;
    }

    /// <summary>Runs a conversion and asserts it succeeded, returning the job.</summary>
    public async Task<ConversionJob> ConvertAsync(string inputPath, string actionKey, IReadOnlyDictionary<string, string>? options = null, string? engineId = null, string? password = null)
    {
        var job = CreateJob(inputPath, actionKey, options, engineId: engineId, password: password);
        if (!job.Engine.Status.IsAvailable)
        {
            Assert.Skip($"Engine {job.Engine.Engine.Id} is not available on this machine: {job.Engine.Status.Reason}");
        }

        await Runner.RunAsync(job, TestContext.Current.CancellationToken);
        Assert.True(job.Status == JobStatus.Succeeded, $"Conversion {actionKey} via {job.Engine.Engine.Id} failed: {job.Message} {job.Diagnostic}");
        Assert.NotEmpty(job.Outputs);
        return job;
    }

    /// <summary>Asserts a file exists and its content (not just its name) is the expected format.</summary>
    public FileFormat AssertFormat(string path, string expectedFormatId)
    {
        Assert.True(File.Exists(path), $"Expected output file {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Output {path} is empty");
        var detected = Detector.Detect(path);
        Assert.True(OutputValidator.Matches(Formats.GetById(expectedFormatId), detected.Format),
            $"Expected {expectedFormatId} but content of {Path.GetFileName(path)} is {detected.Format.Id}");
        return detected.Format;
    }

    public void AssertNoStagingLeft(string directory) =>
        Assert.Empty(Directory.GetDirectories(directory, ConversionRunner.StagingPrefix + "*"));
}
