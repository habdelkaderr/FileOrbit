using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileOrbit.Engines.Office;

/// <summary>
/// Identifies the Office process FileOrbit itself started, so that cancellation or a hung dialog
/// can be resolved by ending that process, and never a copy of Office the user is working in.
/// </summary>
internal sealed partial class OfficeProcessGuard
{
    private readonly string _processName;
    private readonly HashSet<int> _before;

    public OfficeProcessGuard(string processName)
    {
        _processName = processName;
        _before = Snapshot();
        WasAlreadyRunning = _before.Count > 0;
    }

    /// <summary>True when the user already had this Office application open.</summary>
    public bool WasAlreadyRunning { get; }

    public int? OwnedProcessId { get; private set; }

    /// <summary>Call right after creating the automation object.</summary>
    public void Capture(IntPtr windowHandle = default)
    {
        if (windowHandle != IntPtr.Zero && GetWindowThreadProcessId(windowHandle, out var pid) != 0 && !_before.Contains((int)pid))
        {
            OwnedProcessId = (int)pid;
            return;
        }

        var started = Snapshot().Except(_before).ToList();
        if (started.Count == 1)
        {
            OwnedProcessId = started[0];
        }
    }

    /// <summary>Ends the process FileOrbit started. Does nothing if ownership is uncertain.</summary>
    public void KillOwned()
    {
        if (OwnedProcessId is not { } pid)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (string.Equals(process.ProcessName, _processName, StringComparison.OrdinalIgnoreCase))
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private HashSet<int> Snapshot()
    {
        var processes = Process.GetProcessesByName(_processName);
        try
        {
            return processes.Select(p => p.Id).ToHashSet();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
