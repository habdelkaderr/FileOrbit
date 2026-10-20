using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileOrbit.App.Shell;
using FileOrbit.Core.Planning;
using FileOrbit.Core.Settings;
using Microsoft.Win32;

namespace FileOrbit.App.Main;

public partial class SettingsPage : UserControl
{
    private readonly AppServices _services;
    private bool _loading;

    public SettingsPage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        ThemeBox.ItemsSource = new[] { "Use Windows setting", "Dark", "Light" };
        OutputModeBox.ItemsSource = new[] { "Next to the original file", "Always in one folder", "In the folder I last used for that format" };
        CollisionBox.ItemsSource = new[] { "Keep both (add a number)", "Replace the existing file", "Skip the file" };
        ConcurrencyBox.ItemsSource = new[] { 1, 2, 3, 4 };
        Loaded += (_, _) => Load();
    }

    private AppSettings Settings => _services.Settings;

    private void Load()
    {
        _loading = true;
        ThemeBox.SelectedIndex = (int)Settings.Theme;
        ExplorerBox.IsChecked = new ShellRegistration(ShellRegistration.CurrentExecutable).IsRegistered;
        HotkeyEnabledBox.IsChecked = Settings.HotkeyEnabled;
        HotkeyBox.Text = Settings.Hotkey;
        HotkeyBox.IsEnabled = Settings.HotkeyEnabled;
        HotkeyError.Text = (Application.Current as App)?.HotkeyError ?? string.Empty;
        TrayBox.IsChecked = Settings.RunInTray;
        StartupBox.IsChecked = ShellRegistration.IsStartWithWindows();
        NotifyBox.IsChecked = Settings.ShowNotifications;
        OutputModeBox.SelectedIndex = (int)Settings.OutputMode;
        FixedFolderBox.Text = Settings.FixedOutputFolder ?? string.Empty;
        FixedFolderRow.Visibility = Settings.OutputMode == OutputFolderMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        CollisionBox.SelectedIndex = (int)Settings.DefaultCollisionPolicy;
        ConcurrencyBox.SelectedItem = Settings.MaxConcurrentJobs;
        StartImmediatelyBox.IsChecked = Settings.StartWithoutConfirmation;
        ShowUnavailableBox.IsChecked = Settings.ShowUnavailableActions;
        HistoryBox.IsChecked = Settings.KeepHistory;
        DataFolderText.Text = $"Data folder: {_services.DataDirectory}";
        VersionText.Text = $"FileOrbit {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)}";
        _loading = false;
    }

    private void Save() => _services.SaveSettings();

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.Theme = (ThemePreference)ThemeBox.SelectedIndex;
        Save();
        App.Current.Theme.Apply(Settings.Theme);
    }

    private void OnExplorerChanged(object sender, RoutedEventArgs e)
    {
        var registration = new ShellRegistration(ShellRegistration.CurrentExecutable);
        Settings.ExplorerIntegration = ExplorerBox.IsChecked == true;
        try
        {
            if (Settings.ExplorerIntegration)
            {
                registration.Register();
            }
            else
            {
                registration.Unregister();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(Window.GetWindow(this), $"Could not change the Explorer menu: {ex.Message}", "FileOrbit");
        }

        Save();
    }

    private void OnHotkeyEnabledChanged(object sender, RoutedEventArgs e)
    {
        Settings.HotkeyEnabled = HotkeyEnabledBox.IsChecked == true;
        HotkeyBox.IsEnabled = Settings.HotkeyEnabled;
        Save();
        App.Current.ApplyHotkey();
        HotkeyError.Text = (Application.Current as App)?.HotkeyError ?? string.Empty;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab)
        {
            return;
        }

        var gesture = GlobalHotkey.Format(Keyboard.Modifiers, key);
        if (!GlobalHotkey.TryParse(gesture, out _, out _))
        {
            HotkeyError.Text = "Use at least Ctrl, Alt or Win together with a key.";
            return;
        }

        var previous = Settings.Hotkey;
        Settings.Hotkey = gesture;
        App.Current.ApplyHotkey();
        if ((Application.Current as App)?.HotkeyError is { } error)
        {
            Settings.Hotkey = previous;
            App.Current.ApplyHotkey();
            HotkeyError.Text = error;
            return;
        }

        HotkeyBox.Text = gesture;
        HotkeyError.Text = string.Empty;
        Save();
    }

    private void OnTrayChanged(object sender, RoutedEventArgs e)
    {
        Settings.RunInTray = TrayBox.IsChecked == true;
        Save();
        App.Current.ApplyTray();
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        Settings.StartWithWindows = StartupBox.IsChecked == true;
        ShellRegistration.SetStartWithWindows(Settings.StartWithWindows);
        if (Settings.StartWithWindows && !Settings.RunInTray)
        {
            Settings.RunInTray = true;
            TrayBox.IsChecked = true;
            App.Current.ApplyTray();
        }

        Save();
    }

    private void OnSimpleChanged(object sender, RoutedEventArgs e)
    {
        Settings.ShowNotifications = NotifyBox.IsChecked == true;
        Settings.StartWithoutConfirmation = StartImmediatelyBox.IsChecked == true;
        Settings.ShowUnavailableActions = ShowUnavailableBox.IsChecked == true;
        Settings.KeepHistory = HistoryBox.IsChecked == true;
        Save();
    }

    private void OnOutputModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.OutputMode = (OutputFolderMode)OutputModeBox.SelectedIndex;
        FixedFolderRow.Visibility = Settings.OutputMode == OutputFolderMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        if (Settings.OutputMode == OutputFolderMode.Fixed && string.IsNullOrEmpty(Settings.FixedOutputFolder))
        {
            OnChooseFixedFolder(sender, e);
        }

        Save();
    }

    private void OnChooseFixedFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save converted files to" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            Settings.FixedOutputFolder = PathSafety.ValidateOutputDirectory(dialog.FolderName);
            FixedFolderBox.Text = Settings.FixedOutputFolder;
            Save();
        }
        catch (UnsafePathException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "FileOrbit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCollisionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.DefaultCollisionPolicy = (CollisionPolicy)CollisionBox.SelectedIndex;
        Save();
    }

    private void OnConcurrencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ConcurrencyBox.SelectedItem is not int value)
        {
            return;
        }

        Settings.MaxConcurrentJobs = value;
        Save();
    }

    private void OnResetPreferences(object sender, RoutedEventArgs e)
    {
        Settings.Favorites.Clear();
        Settings.Presets.Clear();
        Settings.PreferredEngines.Clear();
        Settings.RememberedFolders.Clear();
        Save();
        MessageBox.Show(Window.GetWindow(this), "Pinned formats and remembered settings were reset.", "FileOrbit");
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Delete the conversion history? Converted files are not affected.", "Clear history",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _services.History.Clear();
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => ShellActions.OpenFolder(_services.Log.LogDirectory);

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _services.ClearLogs();
        MessageBox.Show(Window.GetWindow(this), "Diagnostic logs were deleted.", "FileOrbit");
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => ShellActions.OpenFolder(_services.DataDirectory);

    private void OnNotices(object sender, RoutedEventArgs e)
    {
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(notices))
        {
            ShellActions.OpenFile(notices);
        }
        else
        {
            MessageBox.Show(Window.GetWindow(this), "The notices file is missing from this installation.", "FileOrbit");
        }
    }
}
