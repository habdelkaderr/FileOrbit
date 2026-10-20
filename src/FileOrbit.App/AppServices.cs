using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Formats;
using FileOrbit.Core.Jobs;
using FileOrbit.Core.Logging;
using FileOrbit.Core.Planning;
using FileOrbit.Core.Radial;
using FileOrbit.Core.Settings;
using FileOrbit.Engines;

namespace FileOrbit.App;

/// <summary>Composition root: owns settings, logging, the capability registry, the queue and history.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? SettingsStore.DefaultDataDirectory;
        Directory.CreateDirectory(DataDirectory);
        SettingsStore = new SettingsStore(Path.Combine(DataDirectory, "settings.json"));
        Settings = SettingsStore.Load();
        Log = new FileLog(Path.Combine(DataDirectory, "logs"));
        History = new HistoryStore(Path.Combine(DataDirectory, "history.json"));
        Formats = FormatCatalog.CreateDefault();
        Detector = new FileTypeDetector(Formats);
        Registry = CreateRegistry();
        Runner = new ConversionRunner(new OutputValidator(Detector, EngineCatalog.CreateOutputChecks()), Log);
        Queue = new ConversionQueue(Runner, Settings.MaxConcurrentJobs);
        Queue.JobFinished += (_, job) =>
        {
            if (Settings.KeepHistory && job.Status != JobStatus.Skipped)
            {
                History.Add(job);
            }
        };
        Log.Info("FileOrbit started", ("version", typeof(AppServices).Assembly.GetName().Version), ("engines", Registry.Engines.Count));
    }

    public string DataDirectory { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public FileLog Log { get; }

    public HistoryStore History { get; }

    public FormatCatalog Formats { get; }

    public FileTypeDetector Detector { get; }

    public CapabilityRegistry Registry { get; private set; }

    public ConversionRunner Runner { get; }

    public ConversionQueue Queue { get; }

    public event EventHandler? RegistryChanged;

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Could not save settings");
        }
    }

    /// <summary>Re-detects engines, e.g. after the user installed FFmpeg or changed an engine path.</summary>
    public void RefreshEngines()
    {
        Registry = CreateRegistry();
        RegistryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Validates and detects input paths. Invalid ones are reported, not thrown.</summary>
    public (List<DetectedFile> Files, List<string> Problems) Detect(IEnumerable<string> paths)
    {
        var files = new List<DetectedFile>();
        var problems = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var safe = PathSafety.ValidateInput(path, allowDirectory: true);
                var detected = Detector.Detect(safe);
                if (!detected.IsDirectory && detected.SizeBytes == 0)
                {
                    // e.g. Explorer's "New > Microsoft Word Document" creates a 0-byte file until it is saved in Word.
                    problems.Add($"{detected.FileName} is empty (0 bytes), so there is nothing to convert. " +
                                 "Files created with New > Microsoft Word Document stay empty until you add content and save them in Word.");
                    continue;
                }

                files.Add(detected);
            }
            catch (Exception ex) when (ex is UnsafePathException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return (files, problems);
    }

    public IReadOnlyList<ConversionAction> GetActions(IReadOnlyList<DetectedFile> files) =>
        files.Count == 1 ? Registry.GetActions(files[0].Format) : Registry.GetBatchActions(files.Select(f => f.Format).ToList());

    public RadialMenuModel BuildRadialModel(IReadOnlyList<DetectedFile> files, int slots)
    {
        var actions = GetActions(files);
        return RadialMenuBuilder.Build(actions, Formats, new RadialBuildOptions
        {
            MaxSlots = slots,
            Favorites = Settings.Favorites,
            UsageCounts = History.UsageCounts(files.Count == 1 ? files[0].Format.Id : null),
            IncludeUnavailable = Settings.ShowUnavailableActions && files.Count == 1,
        });
    }

    public bool ToggleFavorite(string actionKey)
    {
        var added = Settings.Favorites.Add(actionKey);
        if (!added)
        {
            Settings.Favorites.Remove(actionKey);
        }

        SaveSettings();
        return added;
    }

    /// <summary>The folder FileOrbit proposes for an output, honouring the user's preference.</summary>
    public string DefaultOutputFolder(DetectedFile input, ConversionAction action)
    {
        var sourceFolder = Path.GetDirectoryName(input.Path.TrimEnd(Path.DirectorySeparatorChar)) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidate = Settings.OutputMode switch
        {
            OutputFolderMode.Fixed when !string.IsNullOrWhiteSpace(Settings.FixedOutputFolder) => Settings.FixedOutputFolder,
            OutputFolderMode.RememberPerFormat when Settings.RememberedFolders.TryGetValue(action.Target.Id, out var remembered) => remembered,
            _ => sourceFolder,
        };

        try
        {
            var validated = PathSafety.ValidateOutputDirectory(candidate!);
            return Directory.Exists(validated) ? validated : sourceFolder;
        }
        catch (UnsafePathException)
        {
            // e.g. the source lives under Program Files: fall back to Documents.
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    public void RememberChoices(ConversionAction action, string folder, IReadOnlyDictionary<string, string> options, string? engineId)
    {
        Settings.RememberedFolders[action.Target.Id] = folder;
        Settings.Presets[action.Key] = new Dictionary<string, string>(options);
        if (engineId is not null)
        {
            Settings.PreferredEngines[action.Key] = engineId;
        }

        SaveSettings();
    }

    /// <summary>The candidate to use for an action: the user's preferred engine if available, else the best one.</summary>
    public EngineCandidate ChooseEngine(ConversionAction action, string? requestedEngineId = null)
    {
        var preferred = requestedEngineId ?? Settings.PreferredEngines.GetValueOrDefault(action.Key);
        return action.AvailableCandidates.FirstOrDefault(c => c.Engine.Id == preferred) ?? action.Best;
    }

    public void ClearLogs() => Log.Clear();

    public void Dispose() => Queue.Dispose();

    /// <summary>Optional engine plugins that could not be loaded (shown on the Engines page).</summary>
    public IReadOnlyList<PluginProblem> PluginProblems { get; private set; } = [];

    private CapabilityRegistry CreateRegistry()
    {
        var (engines, problems) = EngineCatalog.CreateWithPlugins(Settings.EnginePaths, AppContext.BaseDirectory);
        PluginProblems = problems;
        foreach (var problem in problems)
        {
            Log?.Warn("Engine plugin not loaded", ("plugin", problem.AssemblyName), ("reason", problem.Reason));
        }

        return new CapabilityRegistry(Formats, engines);
    }
}
