using System.Text.Json;
using FileOrbit.Core.Jobs;

namespace FileOrbit.Core.Settings;

public sealed record HistoryEntry(
    DateTimeOffset Time,
    string InputPath,
    string SourceFormat,
    string ActionKey,
    string ActionTitle,
    string EngineId,
    JobStatus Status,
    IReadOnlyList<string> Outputs,
    string? Message,
    double DurationSeconds);

/// <summary>Bounded conversion history kept in a local JSON file. Never leaves the machine.</summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 500;
    private readonly string _path;
    private readonly object _gate = new();
    private List<HistoryEntry>? _entries;

    public HistoryStore(string path) => _path = path;

    public event EventHandler? Changed;

    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return EnsureLoaded().ToList();
            }
        }
    }

    public void Add(ConversionJob job)
    {
        var entry = new HistoryEntry(
            job.CompletedAt ?? DateTimeOffset.Now,
            job.Input.Path,
            job.Input.Format.Id,
            job.Action.Key,
            job.Action.Verb,
            job.Engine.Engine.Id,
            job.Status,
            job.Outputs,
            job.Message,
            job.Duration?.TotalSeconds ?? 0);
        Add(entry);
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            var entries = EnsureLoaded();
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            Persist(entries);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How often each action was successfully used, for ranking the radial menu.</summary>
    public IReadOnlyDictionary<string, int> UsageCounts(string? sourceFormatId = null) =>
        Entries
            .Where(e => e.Status == JobStatus.Succeeded && (sourceFormatId is null || e.SourceFormat == sourceFormatId))
            .GroupBy(e => e.ActionKey)
            .ToDictionary(g => g.Key, g => g.Count());

    private List<HistoryEntry> EnsureLoaded()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path), SettingsStore.JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _entries = [];
        }

        return _entries;
    }

    private void Persist(List<HistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, SettingsStore.JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
            // History is best effort.
        }
    }
}
