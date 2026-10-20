using System.Runtime.InteropServices;
using FileOrbit.App.Interop;

namespace FileOrbit.App.Shell;

/// <summary>
/// Reads the files selected in the foreground File Explorer window (or on the desktop) through
/// the Shell automation interfaces, so the global shortcut works on whatever the user selected.
/// Must be called on an STA thread.
/// </summary>
public static class ExplorerSelection
{
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterface = new("000214E2-0000-0000-C000-000000000046");

    public static IReadOnlyList<string> GetSelectedPaths(IntPtr foregroundWindow)
    {
        var className = NativeMethods.GetClassName(foregroundWindow);
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return [];
            }

            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                if (className is "CabinetWClass" or "ExploreWClass")
                {
                    return FromExplorerWindow(shell, foregroundWindow);
                }

                if (className is "Progman" or "WorkerW")
                {
                    return FromDesktop(shell);
                }

                return [];
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception)
        {
            // Selection lookup is best effort (Explorer may be busy or a shell extension may misbehave);
            // the caller falls back to a file picker.
            return [];
        }
    }

    private static IReadOnlyList<string> FromExplorerWindow(dynamic shell, IntPtr hwnd)
    {
        // Windows 11 tabs share one top-level window; the active tab is the first ShellTabWindowClass child.
        var activeTab = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "ShellTabWindowClass", null);
        var tabs = new List<(IntPtr? Tab, IReadOnlyList<string> Selection)>();
        foreach (var window in shell.Windows())
        {
            try
            {
                if (new IntPtr((long)window.HWND) != hwnd)
                {
                    continue;
                }

                tabs.Add((GetTabWindow(window), ReadSelection(window.Document)));
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Not a file-system window (e.g. Control Panel); try the next one.
            }
        }

        // Prefer the active tab; if tabs can't be told apart, use the only tab that has a selection.
        var active = tabs.FirstOrDefault(t => activeTab != IntPtr.Zero && t.Tab == activeTab);
        if (active.Selection is { Count: > 0 })
        {
            return active.Selection;
        }

        var withSelection = tabs.Where(t => t.Selection.Count > 0).ToList();
        return withSelection.Count == 1 ? withSelection[0].Selection : tabs.Count == 1 ? tabs[0].Selection : [];
    }

    private static IReadOnlyList<string> FromDesktop(dynamic shell)
    {
        dynamic windows = shell.Windows();
        object location = null!;
        object root = null!;
        int hwnd;
        // SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1
        dynamic desktop = windows.FindWindowSW(ref location, ref root, 8, out hwnd, 1);
        return desktop is null ? [] : ReadSelection(desktop.Document);
    }

    private static IReadOnlyList<string> ReadSelection(dynamic document)
    {
        var paths = new List<string>();
        foreach (var item in document.SelectedItems())
        {
            string path = item.Path;
            // Skip virtual items (Control Panel entries, libraries) that have no file-system path.
            if (!string.IsNullOrEmpty(path) && !path.StartsWith("::", StringComparison.Ordinal) && (File.Exists(path) || Directory.Exists(path)))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static IntPtr? GetTabWindow(object window)
    {
        if (window is not IServiceProvider provider)
        {
            return null;
        }

        var service = TopLevelBrowserService;
        var iid = ShellBrowserInterface;
        if (provider.QueryService(ref service, ref iid, out var browserPointer) != 0 || browserPointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var browser = (IOleWindow)Marshal.GetObjectForIUnknown(browserPointer);
            return browser.GetWindow(out var tab) == 0 ? tab : null;
        }
        finally
        {
            Marshal.Release(browserPointer);
        }
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
    }

    [ComImport]
    [Guid("00000114-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig]
        int GetWindow(out IntPtr hwnd);

        [PreserveSig]
        int ContextSensitiveHelp(bool enterMode);
    }
}
