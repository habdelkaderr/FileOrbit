using System.Text.Json;
using System.Text.Json.Serialization;
using FileOrbit.Core.Planning;

namespace FileOrbit.Core.Settings;

public enum ThemePreference
{
    System,
    Dark,
    Light,
}

public enum OutputFolderMode
{
    /// <summary>Save next to the source file (default).</summary>
    SameAsSource,

    /// <summary>Always save into <see cref="AppSettings.FixedOutputFolder"/>.</summary>
    Fixed,

    /// <summary>Reuse the folder last chosen for the same output format.</summary>
    RememberPerFormat,
}

/// <summary>User preferences. Stored as JSON under %LOCALAPPDATA%\FileOrbit.</summary>
public sealed class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Global shortcut, e.g. "Ctrl+Alt+Space".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    public OutputFolderMode OutputMode { get; set; } = OutputFolderMode.SameAsSource;

    public string? FixedOutputFolder { get; set; }

    /// <summary>Last output folder per target format id.</summary>
    public Dictionary<string, string> RememberedFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CollisionPolicy DefaultCollisionPolicy { get; set; } = CollisionPolicy.KeepBoth;

    /// <summary>Action keys (e.g. "convert:png") pinned to the first ring.</summary>
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembered option values per action key.</summary>
    public Dictionary<string, Dictionary<string, string>> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Preferred engine per action key, overriding automatic selection.</summary>
    public Dictionary<string, string> PreferredEngines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-specified executable locations for external engines (engine id â†’ path).</summary>
    public Dictionary<string, string> EnginePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Show "Convert with FileOrbit" in the Explorer context menu (per-user registration).</summary>
    public bool ExplorerIntegration { get; set; } = true;

    public bool StartWithWindows { get; set; }

    /// <summary>Keep running in the notification area so the hotkey and context menu respond instantly.</summary>
    public bool RunInTray { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    public bool KeepHistory { get; set; } = true;

    /// <summary>Start immediately when an action has no options that need attention.</summary>
    public bool StartWithoutConfirmation { get; set; }

    public int MaxConcurrentJobs { get; set; } = 2;

    public bool ShowUnavailableActions { get; set; } = true;

    /// <summary>Check GitHub once a day for a newer FileOrbit release (version number only).</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Newest release seen by the last check (tag and page), shown until the user updates.</summary>
    public string? AvailableUpdateTag { get; set; }

    public string? AvailableUpdateUrl { get; set; }

    /// <summary>The release the user was last notified about, so each release is announced once.</summary>
    public string? NotifiedUpdateTag { get; set; }
}

public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string path) => _path = path;

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrbit");

    public string FilePath => _path;

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
                    if (settings is not null)
                    {
                        return Normalize(settings);
                    }
                }
            }
            catch (JsonException)
            {
                // A corrupt settings file must not stop the app; keep a copy for diagnosis and start fresh.
                TryBackupCorrupt();
            }
            catch (IOException)
            {
            }

            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        // Restore case-insensitive comparers lost during deserialization.
        settings.RememberedFolders = new(settings.RememberedFolders ?? [], StringComparer.OrdinalIgnoreCase);
        settings.Favorites = new(settings.Favorites ?? [], StringComparer.OrdinalIgnoreCase);
        settings.Presets = new(settings.Presets ?? [], StringComparer.OrdinalIgnoreCase);
        settings.PreferredEngines = new(settings.PreferredEngines ?? [], StringComparer.OrdinalIgnoreCase);
        settings.EnginePaths = new(settings.EnginePaths ?? [], StringComparer.OrdinalIgnoreCase);
        settings.MaxConcurrentJobs = Math.Clamp(settings.MaxConcurrentJobs, 1, 8);
        return settings;
    }

    private void TryBackupCorrupt()
    {
        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
