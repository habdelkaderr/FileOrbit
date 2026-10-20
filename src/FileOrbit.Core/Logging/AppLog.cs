using System.Text.Json;

namespace FileOrbit.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Minimal structured logger. Properties are written as JSON fields, never file contents.</summary>
public interface IAppLog
{
    void Write(LogLevel level, string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null);
}

public static class AppLogExtensions
{
    public static void Info(this IAppLog log, string message, params (string Key, object? Value)[] properties) =>
        log.Write(LogLevel.Info, message, ToDictionary(properties));

    public static void Warn(this IAppLog log, string message, params (string Key, object? Value)[] properties) =>
        log.Write(LogLevel.Warning, message, ToDictionary(properties));

    public static void Error(this IAppLog log, Exception? exception, string message, params (string Key, object? Value)[] properties) =>
        log.Write(LogLevel.Error, message, ToDictionary(properties), exception);

    public static void Debug(this IAppLog log, string message, params (string Key, object? Value)[] properties) =>
        log.Write(LogLevel.Debug, message, ToDictionary(properties));

    private static Dictionary<string, object?>? ToDictionary((string Key, object? Value)[] properties) =>
        properties.Length == 0 ? null : properties.ToDictionary(p => p.Key, p => p.Value);
}

public sealed class NullLog : IAppLog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null)
    {
    }
}

/// <summary>Writes one JSON object per line to a daily file and keeps a bounded number of days.</summary>
public sealed class FileLog : IAppLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly int _retainDays;

    public FileLog(string directory, int retainDays = 7)
    {
        _directory = directory;
        _retainDays = retainDays;
        System.IO.Directory.CreateDirectory(directory);
        PruneOldFiles();
    }

    public string LogDirectory => _directory;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public void Write(LogLevel level, string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.Now.ToString("O"),
            ["level"] = level.ToString(),
            ["message"] = message,
        };
        if (properties is not null)
        {
            foreach (var (key, value) in properties)
            {
                entry[key] = value?.ToString();
            }
        }

        if (exception is not null)
        {
            entry["exception"] = exception.ToString();
        }

        var line = JsonSerializer.Serialize(entry, JsonOptions);
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, $"fileorbit-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never break a conversion.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "fileorbit-*.log"))
            {
                TryDelete(file);
            }
        }
    }

    private void PruneOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-_retainDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "fileorbit-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
