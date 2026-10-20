using System.Diagnostics;

namespace FileOrbit.App.Shell;

/// <summary>User-initiated "open" actions. FileOrbit never opens converted files on its own.</summary>
public static class ShellActions
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".scr", ".hta", ".cpl", ".lnk", ".pif", ".reg", ".jar", ".appx", ".msix", ".application",
    };

    public static void OpenFile(string path)
    {
        if (Directory.Exists(path))
        {
            OpenFolder(path);
            return;
        }

        if (!File.Exists(path) || ExecutableExtensions.Contains(Path.GetExtension(path)))
        {
            // Never launch something executable, even on request; show it in Explorer instead.
            ShowInFolder(path);
            return;
        }

        Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void ShowInFolder(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            // explorer.exe expects "/select,<path>" as a single argument.
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }

        var folder = Path.GetDirectoryName(path);
        if (folder is not null && Directory.Exists(folder))
        {
            OpenFolder(folder);
        }
    }

    public static void OpenFolder(string folder) =>
        Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private static void Start(ProcessStartInfo info)
    {
        try
        {
            using var _ = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(ex.Message, "FileOrbit", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
