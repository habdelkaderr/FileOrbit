using System.Runtime;
using System.Windows.Threading;
using FileOrbit.App.Interop;
using FileOrbit.Engines.Images;

namespace FileOrbit.App.Shell;

/// <summary>
/// Gives memory back to Windows while FileOrbit waits in the notification area with nothing open: unloads
/// the background-removal model, returns free managed memory and trims the working set. Everything is
/// reloaded on demand, so the next menu still opens normally.
/// </summary>
internal sealed class IdleTrimmer
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isIdle;

    public IdleTrimmer(TimeSpan delay, Func<bool> isIdle)
    {
        _isIdle = isIdle;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (_isIdle())
            {
                Trim();
            }
        };
    }

    /// <summary>Trims after the delay, unless FileOrbit is busy again by then. Repeated calls restart the delay.</summary>
    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    public static void Trim()
    {
        BackgroundRemovalEngine.ReleaseModel();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Aggressive also decommits the freed memory instead of keeping it reserved for later allocations.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);
    }
}
