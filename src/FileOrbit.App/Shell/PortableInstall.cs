using System.Diagnostics;
using FileOrbit.Core.Planning;

namespace FileOrbit.App.Shell;

/// <summary>
/// Lets the portable FileOrbit (e.g. on a USB drive) copy itself onto a PC, so the Explorer menu entry
/// and shortcuts point at a location that still exists after the drive is removed. Per-user only:
/// %LocalAppData%\Programs\FileOrbit, a Start menu shortcut, and HKCU registrations. No admin rights.
/// </summary>
public static class PortableInstall
{
    private const string ShortcutName = "FileOrbit.lnk";

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FileOrbit");

    public static string InstalledExecutable => Path.Combine(InstallDirectory, "FileOrbit.exe");

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);

    /// <summary>True when FileOrbit.exe is running from a USB stick or other removable drive.</summary>
    public static bool IsRunningFromRemovableDrive()
    {
        try
        {
            var root = Path.GetPathRoot(ShellRegistration.CurrentExecutable);
            return root is not null && !root.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(root).DriveType == DriveType.Removable;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }

    /// <summary>True when this process is the copy installed by <see cref="Install"/>.</summary>
    public static bool IsRunningInstalledCopy() =>
        PathSafety.IsSamePath(AppContext.BaseDirectory, InstallDirectory);

    /// <summary>
    /// Copies the running FileOrbit (exe, engine plugins, notes) to the per-user install folder, adds a
    /// Start menu shortcut and returns the installed exe path. Replaces an older installed copy.
    /// </summary>
    public static string Install()
    {
        var source = AppContext.BaseDirectory;
        Directory.CreateDirectory(InstallDirectory);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension is ".exe" or ".dll" or ".md" or ".txt" or ".json")
            {
                File.Copy(file, Path.Combine(InstallDirectory, name), overwrite: true);
            }
        }

        CreateShortcut(ShortcutPath, InstalledExecutable);
        return InstalledExecutable;
    }

    /// <summary>
    /// Removes the installed copy: Explorer entries, start-up entry and Start menu shortcut now, and the
    /// program folder shortly after FileOrbit exits (a running exe cannot delete itself).
    /// Settings and history in %LocalAppData%\FileOrbit are kept.
    /// </summary>
    public static void ScheduleUninstall()
    {
        new ShellRegistration(InstalledExecutable).Unregister();
        ShellRegistration.SetStartWithWindows(false);
        if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }

        // Wait a few seconds for this process to exit, then delete the folder. The path is FileOrbit's own
        // fixed install location, never user input.
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            Arguments = $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{InstallDirectory}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process.Start(info)?.Dispose();
    }

    private static void CreateShortcut(string shortcutPath, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.IconLocation = target + ",0";
            shortcut.Description = "Convert files with a radial menu";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
