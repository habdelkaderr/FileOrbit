using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace FileOrbit.Tests.Support;

/// <summary>
/// One STA thread with one WPF Application for all UI tests (WPF allows a single Application per
/// process). Windows are created off-screen and never activated.
/// </summary>
public static class WpfHost
{
    private static readonly Lazy<Dispatcher> Host = new(Start);
    private static Thread? _thread;

    /// <summary>
    /// Stops the WPF thread cleanly. Without this the thread is torn down abruptly at process exit,
    /// which can crash the test host after all tests have passed.
    /// </summary>
    public static void Shutdown()
    {
        if (!Host.IsValueCreated)
        {
            return;
        }

        var dispatcher = Host.Value;
        try
        {
            dispatcher.Invoke(() =>
            {
                foreach (Window window in Application.Current.Windows.Cast<Window>().ToList())
                {
                    window.Close();
                }

                Application.Current.Shutdown();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
        }

        dispatcher.InvokeShutdown();
        _thread?.Join(TimeSpan.FromSeconds(10));
    }

    public static void Run(Action<Application> work)
    {
        Exception? failure = null;
        Host.Value.Invoke(() =>
        {
            try
            {
                work(Application.Current);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        if (failure is not null)
        {
            throw new Exception("UI test failed on the WPF thread", failure);
        }
    }

    /// <summary>Processes UI work (bindings, layout, async continuations) for a while.</summary>
    public static void Pump(TimeSpan duration, Func<bool>? until = null)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end && until?.Invoke() != true)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/FileOrbit;component/Themes/Styles.xaml") });
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        _thread = thread;
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}

/// <summary>Runs once for the whole test assembly and shuts the shared WPF thread down at the end.</summary>
public sealed class WpfHostFixture : IDisposable
{
    public void Dispose()
    {
        // Release Windows Runtime/COM objects (PDF, OCR, imaging, thumbnails) while their apartments still
        // exist; finalizing them during process teardown can crash the test host after all tests passed.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        WpfHost.Shutdown();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
