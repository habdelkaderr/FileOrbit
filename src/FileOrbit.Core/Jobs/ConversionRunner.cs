using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Logging;
using FileOrbit.Core.Planning;

namespace FileOrbit.Core.Jobs;

/// <summary>
/// Runs one job safely: the engine writes into a private staging folder on the destination volume,
/// outputs are validated, then moved into place without ever touching the source or silently
/// replacing an existing file. Staging is always cleaned up.
/// </summary>
public sealed class ConversionRunner
{
    public const string StagingPrefix = ".fileorbit-";
    private readonly OutputValidator _validator;
    private readonly IAppLog _log;

    public ConversionRunner(OutputValidator validator, IAppLog log)
    {
        _validator = validator;
        _log = log;
    }

    public async Task RunAsync(ConversionJob job, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.Cancellation.Token);
        var token = linked.Token;
        string? staging = null;
        ConversionContext? context = null;
        job.StartedAt = DateTimeOffset.Now;
        job.Status = JobStatus.Running;
        job.Stage = "Preparing";
        _log.Info("Conversion started", ("job", job.Id), ("input", job.Input.Path), ("action", job.Action.Key), ("engine", job.Engine.Engine.Id));

        try
        {
            token.ThrowIfCancellationRequested();
            PathSafety.ValidateInput(job.Input.Path, allowDirectory: job.Input.IsDirectory);
            var outputDirectory = PathSafety.ValidateOutputDirectory(job.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);

            var shape = job.Action.Kind == ActionKind.Extract ? OutputShape.Folder : job.Engine.Capability.Shape;
            var plannedExtension = job.Action.Target.PrimaryExtension;
            if (job.CollisionPolicy == CollisionPolicy.Skip && shape != OutputShape.Folder &&
                OutputPlanner.ResolveCollision(outputDirectory, job.BaseName, plannedExtension, CollisionPolicy.Skip, job.Input.Path) is null)
            {
                job.Status = JobStatus.Skipped;
                job.Message = "Skipped because the output file already exists.";
                return;
            }

            staging = CreateStagingDirectory(outputDirectory);
            context = new ConversionContext
            {
                Input = job.Input,
                Target = job.Action.Target,
                Capability = job.Engine.Capability,
                Options = job.Options,
                StagingDirectory = staging,
                OutputBaseName = job.BaseName,
                Password = job.Password,
                Log = _log,
                Progress = new InlineProgress(p =>
                {
                    job.Progress = p.Fraction;
                    if (p.Stage is not null)
                    {
                        job.Stage = p.Stage;
                    }
                }),
            };

            job.Stage = "Converting";
            await job.Engine.Engine.ConvertAsync(context, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            job.Stage = "Verifying output";
            var outputs = Finalize(job, staging, outputDirectory, shape);
            job.Outputs = outputs;
            job.Progress = 1;
            job.Status = JobStatus.Succeeded;
            job.Stage = "Done";
            job.Message = outputs.Count == 1 ? $"Saved {Path.GetFileName(outputs[0])}" : $"Saved {outputs.Count} files";
            _log.Info("Conversion succeeded", ("job", job.Id), ("outputs", string.Join("|", outputs)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            job.Status = JobStatus.Cancelled;
            job.Stage = "Cancelled";
            job.Message = "Conversion cancelled.";
            _log.Info("Conversion cancelled", ("job", job.Id));
        }
        catch (PasswordRequiredException ex)
        {
            job.NeedsPassword = true;
            Fail(job, ex.Message, ex.Diagnostic, ex);
        }
        catch (ConversionException ex)
        {
            Fail(job, ex.Message, ex.Diagnostic, ex);
        }
        catch (UnsafePathException ex)
        {
            Fail(job, ex.Message, null, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Fail(job, "FileOrbit does not have permission to read the file or write to the destination folder.", ex.Message, ex);
        }
        catch (IOException ex)
        {
            Fail(job, $"A file system error occurred: {ex.Message}", ex.ToString(), ex);
        }
        catch (Exception ex)
        {
            Fail(job, $"The conversion failed unexpectedly: {ex.Message}", ex.ToString(), ex);
        }
        finally
        {
            job.Password = null;
            if (context is not null)
            {
                var warnings = context.Warnings.ToList();
                if (job.Input.Warning is not null)
                {
                    warnings.Insert(0, job.Input.Warning);
                }

                job.Warnings = warnings;
            }

            job.CompletedAt = DateTimeOffset.Now;
            if (staging is not null)
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    private List<string> Finalize(ConversionJob job, string staging, string outputDirectory, OutputShape shape)
    {
        var target = job.Action.Target;
        var producedFiles = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
        var producedEntries = Directory.GetFileSystemEntries(staging);

        if (shape == OutputShape.Folder)
        {
            if (producedEntries.Length == 0)
            {
                throw new ConversionException("The operation finished but produced no files.");
            }

            if (job.Action.Kind == ActionKind.Extract && producedEntries.Length == 1 && File.Exists(producedEntries[0]))
            {
                // An archive holding a single file (e.g. report.txt.gz): deliver the file itself.
                var single = producedEntries[0];
                var name = Path.GetFileNameWithoutExtension(single);
                var extension = Path.GetExtension(single);
                var destination = OutputPlanner.ResolveCollision(outputDirectory, name, extension, CollisionPolicy.KeepBoth, job.Input.Path)!;
                File.Move(single, destination);
                return [destination];
            }

            return [MoveFolder(staging, outputDirectory, job.BaseName, job.Input.Path)];
        }

        if (producedFiles.Length == 0)
        {
            throw new ConversionException("The engine finished but produced no output file.");
        }

        foreach (var file in producedFiles.Where(f => target.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()) || target.Extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
        {
            _validator.Validate(file, target);
        }

        if (producedFiles.Length == 1 && producedEntries.Length == 1)
        {
            var file = producedFiles[0];
            var extension = target.Extensions.FirstOrDefault(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ?? Path.GetExtension(file);
            var destination = OutputPlanner.ResolveCollision(outputDirectory, job.BaseName, extension, job.CollisionPolicy, job.Input.Path)
                ?? throw new ConversionException("The output file already exists and was not replaced.");
            if (PathSafety.IsSamePath(destination, job.Input.Path))
            {
                throw new ConversionException("Refusing to overwrite the source file.");
            }

            File.Move(file, destination, overwrite: job.CollisionPolicy == CollisionPolicy.Replace);
            return [destination];
        }

        if (shape == OutputShape.File)
        {
            throw new ConversionException("The engine produced more files than expected.", string.Join(", ", producedEntries.Select(Path.GetFileName)));
        }

        // Several pages/slides/sheets: deliver them together in a new folder.
        var folder = MoveFolder(staging, outputDirectory, job.BaseName, job.Input.Path);
        return [folder];
    }

    private static string MoveFolder(string staging, string outputDirectory, string baseName, string sourcePath)
    {
        var destination = OutputPlanner.GetAvailablePath(outputDirectory, baseName, string.Empty);
        if (PathSafety.IsSamePath(destination, sourcePath))
        {
            destination = OutputPlanner.GetAvailablePath(outputDirectory, baseName + " (extracted)", string.Empty);
        }

        var entries = Directory.GetFileSystemEntries(staging);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            // An archive that wraps everything in one folder: avoid "Name\Name\..." nesting.
            Directory.Move(entries[0], destination);
            return destination;
        }

        // Move the staged contents into a fresh folder; the (now empty) staging folder is removed later.
        Directory.CreateDirectory(destination);
        foreach (var entry in entries)
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.Move(entry, target);
            }
            else
            {
                File.Move(entry, target);
            }
        }

        return destination;
    }

    private static string CreateStagingDirectory(string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, StagingPrefix + Guid.NewGuid().ToString("N")[..12]);
        var info = Directory.CreateDirectory(path);
        info.Attributes |= FileAttributes.Hidden;
        return path;
    }

    private void Fail(ConversionJob job, string message, string? diagnostic, Exception ex)
    {
        job.Status = JobStatus.Failed;
        job.Stage = "Failed";
        job.Message = message;
        job.Diagnostic = diagnostic;
        _log.Error(ex, "Conversion failed", ("job", job.Id), ("message", message), ("diagnostic", diagnostic));
    }

    internal static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100 * (attempt + 1)); // A just-killed child process may still hold a handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    /// <summary>Progress that invokes synchronously instead of posting to a captured context.</summary>
    private sealed class InlineProgress(Action<ConversionProgress> handler) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => handler(value);
    }
}
