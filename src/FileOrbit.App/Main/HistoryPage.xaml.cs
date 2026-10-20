using System.Windows;
using System.Windows.Controls;
using FileOrbit.App.Shell;
using FileOrbit.Core.Jobs;
using FileOrbit.Core.Settings;

namespace FileOrbit.App.Main;

public sealed record HistoryRow(HistoryEntry Entry)
{
    public string When => Entry.Time.LocalDateTime.ToString("g");

    public string File => Path.GetFileName(Entry.InputPath);

    public string Result => Entry.Status == JobStatus.Succeeded
        ? string.Join(", ", Entry.Outputs.Select(Path.GetFileName))
        : $"{Entry.Status}: {Entry.Message}";
}

public partial class HistoryPage : UserControl
{
    private readonly AppServices _services;

    public HistoryPage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        services.History.Changed += (_, _) => Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        var rows = _services.History.Entries
            .Where(e => query.Length == 0
                        || e.InputPath.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || e.ActionTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || e.Outputs.Any(o => o.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new HistoryRow(e))
            .ToList();
        HistoryList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Refresh();

    private void OnShow(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is HistoryRow row)
        {
            ShellActions.ShowInFolder(row.Entry.Outputs.FirstOrDefault() ?? row.Entry.InputPath);
        }
    }

    private void OnAgain(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is HistoryRow row)
        {
            App.Current.ShowRadial([row.Entry.InputPath]);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Delete the conversion history? Converted files are not affected.", "Clear history",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _services.History.Clear();
        }
    }
}
