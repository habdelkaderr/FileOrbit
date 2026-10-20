using System.Diagnostics;
using System.Text;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Logging;

namespace FileOrbit.Engines.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed)
{
    /// <summary>The last part of stderr, suitable for a diagnostic log entry.</summary>
    public string ErrorTail(int maxChars = 2000) =>
        StandardError.Length <= maxChars ? StandardError : StandardError[^maxChars..];
}

/// <summary>
/// Runs external tools without a shell: arguments are passed as a list (no string concatenation,
/// no command injection), the window is hidden, output is captured, and the whole process tree is
/// killed on cancellation or timeout. Children are bound to a Job Object so they also die if
/// FileOrbit exits unexpectedly.
/// </summary>
public static class ProcessRunner
{
    private const int MaxCapturedChars = 1_000_000;

    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onStandardOutputLine = null,
        Action<string>? onStandardErrorLine = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IAppLog? log = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new ConversionException($"The tool '{Path.GetFileName(executable)}' could not be found.", executable);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            Append(stdout, e.Data);
            onStandardOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            Append(stderr, e.Data);
            onStandardErrorLine?.Invoke(e.Data);
        };

        var stopwatch = Stopwatch.StartNew();
        log?.Debug("Starting process", ("exe", executable), ("args", string.Join(" ", startInfo.ArgumentList.Select(Quote))));
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ConversionException($"Could not start {Path.GetFileName(executable)}.", ex.Message, ex);
        }

        ChildProcessJob.Instance.TryAssign(process);
        process.StandardInput.Close(); // Tools must never wait for interactive input.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = timeout is null ? new CancellationTokenSource() : new CancellationTokenSource(timeout.Value);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ConversionException($"{Path.GetFileName(executable)} did not finish within {timeout!.Value.TotalMinutes:0} minutes and was stopped.");
            }

            throw;
        }

        // Ensure the asynchronous readers have drained.
        process.WaitForExit();
        stopwatch.Stop();
        log?.Debug("Process exited", ("exe", Path.GetFileName(executable)), ("exitCode", process.ExitCode), ("ms", stopwatch.ElapsedMilliseconds));
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed);
    }

    private static void Append(StringBuilder builder, string line)
    {
        lock (builder)
        {
            if (builder.Length < MaxCapturedChars)
            {
                builder.AppendLine(line);
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string Quote(string argument) => argument.Contains(' ') ? $"\"{argument}\"" : argument;
}
