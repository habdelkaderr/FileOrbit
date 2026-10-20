using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FileOrbit.App.Shell;
using FileOrbit.Core.Jobs;

namespace FileOrbit.App.Main;

public partial class QueuePage : UserControl
{
    private readonly AppServices _services;
    private readonly ObservableCollection<ConversionJob> _jobs;

    public QueuePage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _jobs = new ObservableCollection<ConversionJob>(services.Queue.Jobs.Reverse());
        JobList.ItemsSource = _jobs;
        _jobs.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();

        services.Queue.JobAdded += OnJobAdded;
        services.Queue.JobRemoved += OnJobRemoved;
        Unloaded += (_, _) =>
        {
            services.Queue.JobAdded -= OnJobAdded;
            services.Queue.JobRemoved -= OnJobRemoved;
        };
        Loaded += (_, _) =>
        {
            services.Queue.JobAdded -= OnJobAdded;
            services.Queue.JobRemoved -= OnJobRemoved;
            services.Queue.JobAdded += OnJobAdded;
            services.Queue.JobRemoved += OnJobRemoved;
            Resync();
        };
    }

    private void Resync()
    {
        _jobs.Clear();
        foreach (var job in _services.Queue.Jobs.Reverse())
        {
            _jobs.Add(job);
        }
    }

    private void OnJobAdded(object? sender, ConversionJob job) => Dispatcher.BeginInvoke(() => _jobs.Insert(0, job));

    private void OnJobRemoved(object? sender, ConversionJob job) => Dispatcher.BeginInvoke(() => _jobs.Remove(job));

    private void UpdateEmpty() => EmptyText.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static ConversionJob? JobOf(object sender) => ((FrameworkElement)sender).Tag as ConversionJob;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job)
        {
            _services.Queue.Cancel(job);
        }
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } job)
        {
            return;
        }

        string? password = null;
        if (job.NeedsPassword)
        {
            password = PasswordPrompt.Ask(Window.GetWindow(this), job.Input.FileName);
            if (password is null)
            {
                return;
            }
        }

        _ = _services.Queue.Retry(job, password);
    }

    private void OnShow(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job)
        {
            ShellActions.ShowInFolder(job.PrimaryOutput ?? job.Input.Path);
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job)
        {
            _services.Queue.Remove(job);
        }
    }

    private void OnClearFinished(object sender, RoutedEventArgs e) => _services.Queue.ClearFinished();

    private void OnCancelAll(object sender, RoutedEventArgs e) => _services.Queue.CancelAll();
}
