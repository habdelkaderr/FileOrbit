using System.Windows;
using System.Windows.Threading;
using FileOrbit.App.Conversion;
using FileOrbit.App.Interop;
using FileOrbit.App.Main;
using FileOrbit.App.Radial;
using FileOrbit.App.Shell;
using FileOrbit.App.Theming;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Jobs;
using FileOrbit.Core.Logging;
using FileOrbit.Core.Shell;
using Microsoft.Win32;

namespace FileOrbit.App;

/// <summary>
/// Application controller: routes launches (context menu, hotkey, drag and drop, command line) to
/// the radial menu, and owns the long-lived pieces (tray icon, hotkey, single-instance channel).
/// </summary>
public partial class App
{
    private readonly InstanceChannel _instance;
    private readonly CommandLine _initialCommand;
    private readonly List<string> _pendingPaths = [];
    private readonly DispatcherTimer _batchTimer;
    private NativeMethods.NativePoint? _pendingAnchor;
    private RadialMenuWindow? _radial;
    private MainWindow? _main;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;

    public App(InstanceChannel instance, CommandLine initialCommand)
    {
        _instance = instance;
        _initialCommand = initialCommand;
        // Explorer starts one FileOrbit process per selected file; group arrivals into one batch.
        _batchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _batchTimer.Tick += (_, _) => FlushPendingPaths();
    }

    public static new App Current => (App)Application.Current;

    public AppServices Services { get; private set; } = null!;

    public ThemeManager Theme { get; private set; } = null!;

    public string? HotkeyError { get; private set; }

    public UpdateService Updates { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Services = new AppServices();
        Theme = new ThemeManager(this);
        Theme.Apply(Services.Settings.Theme);

        _tray = new TrayIcon(() => ShowMain(), ConvertViaPicker, () => ShowMain(MainPage.Settings), RequestExit)
        {
            Persistent = Services.Settings.RunInTray,
        };
        ApplyHotkey();
        Updates = new UpdateService(Services, release => _tray?.Notify(
            $"FileOrbit {release.Version.ToString(3)} is available",
            "Click to see what's new and download the update.",
            false,
            () => UpdateService.OpenReleasePage(release)));
        var fromRemovableDrive = PortableInstall.IsRunningFromRemovableDrive();
        if (!fromRemovableDrive)
        {
            // From a USB drive, registering the Explorer entry would leave a broken menu item once the drive is removed.
            EnsureShellIntegration();
        }

        Services.Queue.JobFinished += (_, job) => Dispatcher.BeginInvoke(() => OnJobFinished(job));

        _instance.StartServer(command => Dispatcher.BeginInvoke(() => Handle(command)));
        Handle(_initialCommand);
        if (fromRemovableDrive && _initialCommand.Mode != LaunchMode.Background)
        {
            Dispatcher.BeginInvoke(OfferInstallFromRemovableDrive, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Set when FileOrbit should start another copy after this one exits (the freshly installed one).</summary>
    public string? RelaunchExecutable { get; private set; }

    private void OfferInstallFromRemovableDrive()
    {
        var drive = Path.GetPathRoot(ShellRegistration.CurrentExecutable);
        var answer = MessageBox.Show(
            $"FileOrbit is running from a removable drive ({drive}).\n\n" +
            "Install it on this PC? It is copied to your user folder (no administrator rights needed), so the " +
            "right-click menu and the keyboard shortcut keep working after you remove the drive.\n\n" +
            "Choose No to use it from the drive this time only. The right-click menu entry is available after installing.",
            "Install FileOrbit on this PC?", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var installed = PortableInstall.Install();
            if (Services.Settings.ExplorerIntegration)
            {
                new ShellRegistration(installed).Register();
            }

            Services.Log.Info("Installed from removable drive", ("target", installed));
            RelaunchExecutable = installed;
            Shutdown();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Services.Log.Error(ex, "Install from removable drive failed");
            MessageBox.Show(
                $"FileOrbit could not be installed: {ex.Message}\n\nIf FileOrbit is already installed and running on this PC, exit it from its notification-area icon and try again.",
                "FileOrbit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Removes the installed copy from this PC and exits.</summary>
    public void UninstallFromThisPc()
    {
        Services.Queue.CancelAll();
        PortableInstall.ScheduleUninstall();
        Services.Log.Info("Uninstalled from this PC");
        Shutdown();
    }

    public void Handle(CommandLine command)
    {
        switch (command.Mode)
        {
            case LaunchMode.Open when command.Paths.Count > 0:
                if (_pendingPaths.Count == 0)
                {
                    NativeMethods.GetCursorPos(out var cursor);
                    _pendingAnchor = cursor;
                }

                _pendingPaths.AddRange(command.Paths);
                _batchTimer.Stop();
                _batchTimer.Start();
                break;
            case LaunchMode.Background:
                if (!Services.Settings.RunInTray)
                {
                    // Started with Windows but the tray is disabled: nothing to do.
                    Shutdown();
                }

                break;
            default:
                ShowMain();
                break;
        }
    }

    private void FlushPendingPaths()
    {
        _batchTimer.Stop();
        var paths = _pendingPaths.ToList();
        _pendingPaths.Clear();
        ShowRadial(paths, _pendingAnchor);
    }

    /// <summary>Validates paths and opens the radial menu for them at <paramref name="anchor"/> (or the cursor).</summary>
    public void ShowRadial(IReadOnlyList<string> paths, NativeMethods.NativePoint? anchor = null)
    {
        var (files, problems) = Services.Detect(paths);
        if (problems.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, problems), "Some items cannot be converted", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (files.Count > 0)
        {
            ShowRadial(files, anchor);
        }
        else
        {
            ExitIfIdle();
        }
    }

    public void ShowRadial(IReadOnlyList<DetectedFile> files, NativeMethods.NativePoint? anchor = null)
    {
        _radial?.Close();
        var actions = Services.GetActions(files);
        if (actions.Count == 0)
        {
            var what = files.Count == 1 ? $"“{files[0].FileName}” ({files[0].Format.FullName})" : "these files together";
            MessageBox.Show($"FileOrbit has no conversions for {what}.\n\nSee the conversion catalog for everything FileOrbit supports.",
                "FileOrbit", MessageBoxButton.OK, MessageBoxImage.Information);
            ExitIfIdle();
            return;
        }

        var radial = new RadialMenuWindow(new RadialSession
        {
            Files = files,
            Anchor = anchor,
            BuildModel = slots => Services.BuildRadialModel(files, slots),
            ActionChosen = action => OpenConversion(files, action, anchor),
            UnavailableChosen = action => ShowMissingDependency(action),
            ToggleFavorite = Services.ToggleFavorite,
        });
        radial.Closed += (_, _) =>
        {
            if (_radial == radial)
            {
                _radial = null;
            }

            Dispatcher.BeginInvoke(ExitIfIdle, DispatcherPriority.ApplicationIdle);
        };
        _radial = radial;
        radial.Show();
    }

    public void OpenConversion(IReadOnlyList<DetectedFile> files, ConversionAction action, NativeMethods.NativePoint? anchor)
    {
        var window = new ConversionWindow(Services, files, action, anchor, (again, point) => ShowRadial(again, point));
        window.Closed += (_, _) => Dispatcher.BeginInvoke(ExitIfIdle, DispatcherPriority.ApplicationIdle);
        window.Show();
    }

    private void ShowMissingDependency(ConversionAction action)
    {
        var dependency = action.MissingDependency;
        var message = $"{action.Verb} is not available: {action.UnavailableReason}";
        if (dependency is not null)
        {
            message += $"\n\n{dependency.Name} adds: {dependency.Purpose}.\nOpen the Engines page to see how to install it?";
            if (MessageBox.Show(message, "FileOrbit", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                ShowMain(MainPage.Engines);
                return;
            }
        }
        else
        {
            MessageBox.Show(message, "FileOrbit", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        ExitIfIdle();
    }

    public void ShowMain(MainPage page = MainPage.Home)
    {
        _main ??= CreateMain();
        _main.Navigate(page);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    private MainWindow CreateMain()
    {
        var window = new MainWindow(Services);
        window.Closed += (_, _) =>
        {
            _main = null;
            Dispatcher.BeginInvoke(ExitIfIdle, DispatcherPriority.ApplicationIdle);
        };
        return window;
    }

    public void ConvertViaPicker()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to convert" };
        if (dialog.ShowDialog() == true)
        {
            ShowRadial(dialog.FileNames);
        }
        else
        {
            ExitIfIdle();
        }
    }

    public void ApplyHotkey()
    {
        _hotkey ??= CreateHotkey();
        HotkeyError = null;
        if (Services.Settings.HotkeyEnabled && !_hotkey.TryRegister(Services.Settings.Hotkey, out var error))
        {
            HotkeyError = error;
            Services.Log.Warn("Hotkey registration failed", ("hotkey", Services.Settings.Hotkey), ("error", error));
        }
        else if (!Services.Settings.HotkeyEnabled)
        {
            _hotkey.Unregister();
        }
    }

    private GlobalHotkey CreateHotkey()
    {
        var hotkey = new GlobalHotkey();
        hotkey.Pressed += (_, _) =>
        {
            var foreground = NativeMethods.GetForegroundWindow();
            var selection = ExplorerSelection.GetSelectedPaths(foreground);
            if (selection.Count > 0)
            {
                ShowRadial(selection);
            }
            else
            {
                ConvertViaPicker();
            }
        };
        return hotkey;
    }

    public void ApplyTray() => _tray!.Persistent = Services.Settings.RunInTray;

    /// <summary>Registers the Explorer verb on first run, and repairs it if FileOrbit.exe has moved.</summary>
    private void EnsureShellIntegration()
    {
        try
        {
            var registration = new ShellRegistration(ShellRegistration.CurrentExecutable);
            if (Services.Settings.ExplorerIntegration && (!registration.IsRegistered || registration.IsStale))
            {
                registration.Register();
            }

            if (Services.Settings.StartWithWindows != ShellRegistration.IsStartWithWindows() || Services.Settings.StartWithWindows)
            {
                ShellRegistration.SetStartWithWindows(Services.Settings.StartWithWindows);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Services.Log.Error(ex, "Could not update Explorer integration");
        }
    }

    private void OnJobFinished(ConversionJob job)
    {
        if (!Services.Settings.ShowNotifications || job.Status is JobStatus.Skipped)
        {
            return;
        }

        // The conversion card already shows the result when it is in front of the user.
        var visibleCard = Windows.OfType<ConversionWindow>().Any(w => w.IsActive && w.Jobs.Contains(job));
        if (visibleCard)
        {
            return;
        }

        var output = job.PrimaryOutput;
        switch (job.Status)
        {
            case JobStatus.Succeeded:
                _tray?.Notify("Conversion complete", $"{job.Input.FileName} → {Path.GetFileName(output)}", false,
                    output is null ? null : () => ShellActions.ShowInFolder(output));
                break;
            case JobStatus.Failed:
                _tray?.Notify("Conversion failed", $"{job.Input.FileName}: {job.Message}", true, () => ShowMain(MainPage.Queue));
                break;
            case JobStatus.Cancelled:
                _tray?.Notify("Conversion cancelled", job.Input.FileName, false, null);
                break;
        }
    }

    /// <summary>Without the tray icon, FileOrbit exits once no window is open and no conversion is running.</summary>
    private void ExitIfIdle()
    {
        if (Services.Settings.RunInTray || Windows.Count > 0 || Services.Queue.HasActiveJobs || _pendingPaths.Count > 0)
        {
            if (!Services.Settings.RunInTray && Windows.Count == 0 && Services.Queue.HasActiveJobs)
            {
                // Re-check when the remaining conversions finish.
                Services.Queue.JobFinished -= ExitWhenQueueDrains;
                Services.Queue.JobFinished += ExitWhenQueueDrains;
            }

            return;
        }

        Shutdown();
    }

    private void ExitWhenQueueDrains(object? sender, ConversionJob job)
    {
        if (!Services.Queue.HasActiveJobs)
        {
            Services.Queue.JobFinished -= ExitWhenQueueDrains;
            Dispatcher.BeginInvoke(ExitIfIdle, DispatcherPriority.ApplicationIdle);
        }
    }

    public void RequestExit()
    {
        if (Services.Queue.HasActiveJobs &&
            MessageBox.Show("Conversions are still running. Stop them and exit?", "FileOrbit", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Queue.CancelAll();
        _hotkey?.Dispose();
        Updates?.Dispose();
        _tray?.Dispose();
        Services?.Log.Info("FileOrbit exiting");
        Services?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services?.Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nDetails were written to the log.", "FileOrbit", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
