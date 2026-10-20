using System.Windows;
using System.Windows.Controls;
using FileOrbit.App.Shell;
using FileOrbit.Core.Jobs;
using FileOrbit.Core.Settings;

namespace FileOrbit.App.Main;

public sealed record RecentItem(HistoryEntry Entry)
{
    public string Title => $"{Path.GetFileName(Entry.InputPath)} → {Entry.ActionTitle}";

    public string Detail => $"{Entry.Time.LocalDateTime:g} · {string.Join(", ", Entry.Outputs.Select(Path.GetFileName))}";
}

public partial class HomePage : UserControl
{
    private readonly AppServices _services;

    public HomePage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        services.History.Changed += (_, _) => Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var registration = new ShellRegistration(ShellRegistration.CurrentExecutable);
        ContextMenuStatus.Text = registration.IsRegistered
            ? "Right-click any file or folder and choose “Convert with FileOrbit”. On Windows 11 it is under “Show more options” (Shift+F10)."
            : "The Explorer menu entry is turned off. Turn it on in Settings.";
        HotkeyStatus.Text = !_services.Settings.HotkeyEnabled
            ? "The global shortcut is turned off. Turn it on in Settings."
            : (Application.Current as App)?.HotkeyError ?? $"Select files in File Explorer and press {_services.Settings.Hotkey}.";

        var recent = _services.History.Entries.Where(e => e.Status == JobStatus.Succeeded).Take(6).Select(e => new RecentItem(e)).ToList();
        RecentList.ItemsSource = recent;
        EmptyRecent.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowse(object sender, RoutedEventArgs e) => App.Current.ConvertViaPicker();

    private void OnViewHistory(object sender, RoutedEventArgs e) => App.Current.ShowMain(MainPage.History);

    private void OnShowRecent(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is RecentItem item && item.Entry.Outputs.FirstOrDefault() is { } output)
        {
            ShellActions.ShowInFolder(output);
        }
    }

    private void OnAgainRecent(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is RecentItem item)
        {
            App.Current.ShowRadial([item.Entry.InputPath]);
        }
    }
}
