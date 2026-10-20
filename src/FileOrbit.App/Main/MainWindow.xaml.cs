using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using FileOrbit.App.Interop;
using FileOrbit.App.Theming;
using FileOrbit.Core.Jobs;

namespace FileOrbit.App.Main;

public enum MainPage
{
    Home,
    Queue,
    History,
    Catalog,
    Engines,
    Settings,
}

public sealed class NavItem(MainPage page, string title, string glyph) : INotifyPropertyChanged
{
    private string? _badge;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainPage Page { get; } = page;

    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public string? Badge
    {
        get => _badge;
        set
        {
            _badge = value;
            OnChanged();
            OnChanged(nameof(BadgeVisibility));
        }
    }

    public Visibility BadgeVisibility => string.IsNullOrEmpty(_badge) ? Visibility.Collapsed : Visibility.Visible;

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly Dictionary<MainPage, FrameworkElement> _pages = [];
    private readonly List<NavItem> _navItems =
    [
        new(MainPage.Home, "Home", Glyphs.Home),
        new(MainPage.Queue, "Queue", Glyphs.Queue),
        new(MainPage.History, "History", Glyphs.History),
        new(MainPage.Catalog, "Catalog", Glyphs.Catalog),
        new(MainPage.Engines, "Engines", Glyphs.Engines),
        new(MainPage.Settings, "Settings", Glyphs.Settings),
    ];

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Nav.ItemsSource = _navItems;
        DragEnter += OnDragEnter;
        DragOver += OnDragEnter;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;

        services.Queue.JobAdded += OnQueueChanged;
        services.Queue.JobFinished += OnQueueChanged;
        Closed += (_, _) =>
        {
            services.Queue.JobAdded -= OnQueueChanged;
            services.Queue.JobFinished -= OnQueueChanged;
        };
        UpdateQueueBadge();
    }

    public void Navigate(MainPage page) => Nav.SelectedItem = _navItems.First(i => i.Page == page);

    private void OnNavigate(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not NavItem item)
        {
            return;
        }

        if (!_pages.TryGetValue(item.Page, out var page))
        {
            page = item.Page switch
            {
                MainPage.Home => new HomePage(_services),
                MainPage.Queue => new QueuePage(_services),
                MainPage.History => new HistoryPage(_services),
                MainPage.Catalog => new CatalogPage(_services),
                MainPage.Engines => new EnginesPage(_services),
                _ => new SettingsPage(_services),
            };
            _pages[item.Page] = page;
        }

        PageHost.Content = page;
    }

    private void OnQueueChanged(object? sender, ConversionJob job) => Dispatcher.BeginInvoke(UpdateQueueBadge);

    private void UpdateQueueBadge()
    {
        var active = _services.Queue.Jobs.Count(j => !j.IsFinished);
        _navItems.First(i => i.Page == MainPage.Queue).Badge = active > 0 ? active.ToString() : null;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        var isFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = isFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = isFiles ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            NativeMethods.GetCursorPos(out var cursor);
            App.Current.ShowRadial(paths, cursor);
        }
    }
}
