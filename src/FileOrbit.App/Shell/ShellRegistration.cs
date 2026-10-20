using FileOrbit.App.Interop;
using FileOrbit.Core.Shell;
using Microsoft.Win32;

namespace FileOrbit.App.Shell;

/// <summary>
/// Per-user Explorer integration (HKEY_CURRENT_USER only: no admin rights, fully reversible).
/// Adds "Convert with FileOrbit" for files and folders. On Windows 11 it appears under
/// "Show more options" (Shift+F10), the location available to unpackaged applications.
/// </summary>
public sealed class ShellRegistration
{
    public const string VerbName = "FileOrbit";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int ShcneAssocChanged = 0x08000000;

    private static readonly (string Key, string Label)[] Verbs =
    [
        (@"Software\Classes\*\shell\" + VerbName, "Convert with FileOrbit"),
        (@"Software\Classes\Directory\shell\" + VerbName, "Compress with FileOrbit"),
    ];

    private readonly string _executable;

    public ShellRegistration(string executable) => _executable = executable;

    public static string CurrentExecutable => Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the FileOrbit executable path.");

    public string Command => $"\"{_executable}\" {CommandLine.OpenSwitch} \"%1\"";

    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Verbs[0].Key + @"\command");
            return key?.GetValue(null) is string command && string.Equals(command, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>True when a registration exists but points at a different FileOrbit.exe (moved or updated).</summary>
    public bool IsStale
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Verbs[0].Key + @"\command");
            return key?.GetValue(null) is string command && !string.Equals(command, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Register()
    {
        foreach (var (keyPath, label) in Verbs)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(keyPath);
            verb.SetValue(null, label);
            verb.SetValue("Icon", $"\"{_executable}\",0");
            // "Player" lets Explorer invoke the verb for any number of selected items (each launch is
            // forwarded to the running instance and grouped into one batch).
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, Command);
        }

        NotifyShell();
    }

    public void Unregister()
    {
        foreach (var (keyPath, _) in Verbs)
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }

        NotifyShell();
    }

    public static bool IsStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(VerbName) is string;
    }

    public static void SetStartWithWindows(bool enabled, string? executable = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(VerbName, $"\"{executable ?? CurrentExecutable}\" {CommandLine.BackgroundSwitch}");
        }
        else
        {
            key.DeleteValue(VerbName, throwOnMissingValue: false);
        }
    }

    private static void NotifyShell() => NativeMethods.SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
}
