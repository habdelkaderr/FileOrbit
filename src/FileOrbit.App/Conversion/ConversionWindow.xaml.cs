using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FileOrbit.App.Interop;
using FileOrbit.App.Radial;
using FileOrbit.App.Shell;
using FileOrbit.App.Theming;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Jobs;
using FileOrbit.Core.Planning;
using Microsoft.Win32;

namespace FileOrbit.App.Conversion;

/// <summary>
/// The card shown after choosing an action: shows what will happen, asks only for relevant
/// settings, then follows the job(s) through progress to a result with useful next steps.
/// </summary>
public partial class ConversionWindow : Window
{
    private readonly AppServices _services;
    private readonly IReadOnlyList<DetectedFile> _files;
    private readonly ConversionAction _action;
    private readonly NativeMethods.NativePoint? _anchor;
    private readonly Action<IReadOnlyList<DetectedFile>, NativeMethods.NativePoint?> _convertAgain;
    private readonly Dictionary<string, string> _optionValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ConversionJob> _jobs = [];
    private string? _folder;
    private EngineCandidate _engine;
    private bool _started;
    private readonly bool _preview;

    public ConversionWindow(AppServices services, IReadOnlyList<DetectedFile> files, ConversionAction action,
        NativeMethods.NativePoint? anchor, Action<IReadOnlyList<DetectedFile>, NativeMethods.NativePoint?> convertAgain, bool preview = false)
    {
        _preview = preview;
        _services = services;
        _files = files;
        _action = action;
        _anchor = anchor;
        _convertAgain = convertAgain;
        _engine = services.ChooseEngine(action);
        InitializeComponent();

        Title = $"{action.Verb} — FileOrbit";
        BatchList.ItemsSource = _jobs;
        InitializeHeader();
        InitializeEngines();
        BuildOptions();
        InitializeDestination();
        UpdateFidelity();
        if (preview)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            Left = -20000;
            Top = -20000;
        }
        else
        {
            Loaded += OnLoaded;
        }
    }

    public IReadOnlyList<ConversionJob> Jobs => _jobs;

    private bool IsBatch => _files.Count > 1;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceNearAnchor();
        if (_services.Settings.StartWithoutConfirmation && OptionsPanel.Children.Count == 0 && ConflictRow.Visibility != Visibility.Visible)
        {
            OnConvert(this, new RoutedEventArgs());
        }
        else
        {
            ConvertButton.Focus();
        }
    }

    private void PlaceNearAnchor()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var anchor = _anchor ?? cursor;
        var workArea = NativeMethods.GetWorkArea(anchor, out var scale);
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var x = Math.Clamp(anchor.X - width / 2, workArea.Left + 8, Math.Max(workArea.Left + 8, workArea.Right - width - 8));
        var y = Math.Clamp(anchor.Y - (int)(160 * scale), workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - height - 8));
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NativeMethods.SwpNoZOrder | 0x0001 /* SWP_NOSIZE */);
        NativeMethods.SetForegroundWindow(hwnd);
        Activate();
    }

    private void InitializeHeader()
    {
        if (IsBatch)
        {
            SourceName.Text = $"{_files.Count} files";
            var formats = _files.Select(f => f.Format.Name).Distinct().ToList();
            SourceMeta.Text = $"{string.Join(", ", formats.Take(4))}{(formats.Count > 4 ? "…" : string.Empty)} · {RadialMenuWindow.FormatSize(_files.Sum(f => f.SizeBytes))}";
            SourceGlyph.Text = Glyphs.Files;
        }
        else
        {
            var file = _files[0];
            SourceName.Text = file.FileName;
            SourceName.ToolTip = file.Path;
            SourceMeta.Text = file.IsDirectory ? "Folder" : $"{file.Format.FullName} · {RadialMenuWindow.FormatSize(file.SizeBytes)}";
            if (file.Warning is not null)
            {
                SourceMeta.Text += " · content differs from extension";
                SourceMeta.ToolTip = file.Warning;
            }

            SourceGlyph.Text = Glyphs.ForFormat(file.Format);
            _ = LoadThumbnailAsync(file.Path);
        }

        TargetGlyph.Text = Glyphs.ForItem(new Core.Radial.RadialItem(_action.Key, Core.Radial.RadialItemKind.Action, _action.Title, string.Empty, _action.Category, _action));
        TargetGlyph.SetResourceReference(ForegroundProperty, Glyphs.CategoryBrushKey(_action.Category));
        TargetName.Text = _action.Kind == ActionKind.Convert ? _action.Target.Name : _action.Title;
        TargetName.ToolTip = _action.Target.FullName;
    }

    private async Task LoadThumbnailAsync(string path)
    {
        var image = await ShellThumbnail.GetAsync(path, 88);
        if (image is not null)
        {
            SourceImage.Source = image;
            SourceGlyph.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record EngineChoice(EngineCandidate Candidate, string Display);

    private void InitializeEngines()
    {
        var choices = _action.AvailableCandidates
            .Select(c => new EngineChoice(c, $"{c.Engine.DisplayName} — {ShortFidelity(c.Capability.Fidelity)}"))
            .ToList();
        if (choices.Count > 1)
        {
            EngineRow.Visibility = Visibility.Visible;
            EngineBox.ItemsSource = choices;
            EngineBox.SelectedItem = choices.FirstOrDefault(c => c.Candidate.Engine.Id == _engine.Engine.Id) ?? choices[0];
        }
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EngineBox.SelectedItem is EngineChoice choice && choice.Candidate != _engine)
        {
            _engine = choice.Candidate;
            BuildOptions();
            UpdateFidelity();
        }
    }

    private void UpdateFidelity()
    {
        var capability = _engine.Capability;
        var (glyph, title, brush) = capability.Fidelity switch
        {
            Fidelity.Lossless => (Glyphs.Check, "Lossless", "OrbitSuccessBrush"),
            Fidelity.Repackage => (Glyphs.Check, "Contents are kept unchanged", "OrbitSuccessBrush"),
            Fidelity.Lossy => (Glyphs.Warning, "Lossy: smaller files, some quality loss", "OrbitWarningBrush"),
            Fidelity.Extraction => (Glyphs.Warning, "Extracts content only; layout is not preserved", "OrbitWarningBrush"),
            Fidelity.LayoutSensitive => (Glyphs.Warning, "Layout is recreated and may differ from the original", "OrbitWarningBrush"),
            _ => (Glyphs.Warning, "Converted to pictures; text will not be editable", "OrbitWarningBrush"),
        };
        FidelityGlyph.Text = glyph;
        FidelityGlyph.SetResourceReference(ForegroundProperty, brush);
        FidelityTitle.Text = title;
        FidelityNote.Text = capability.Note is null ? string.Empty : capability.Note + ".";
        FidelityNote.Visibility = capability.Note is null ? Visibility.Collapsed : Visibility.Visible;
        PrivacyNote.Text = $"Runs on this PC with {_engine.Engine.DisplayName}. Nothing is uploaded.";
    }

    private void BuildOptions()
    {
        OptionsPanel.Children.Clear();
        AdvancedPanel.Children.Clear();
        _services.Settings.Presets.TryGetValue(_action.Key, out var remembered);
        foreach (var descriptor in _engine.Capability.Options)
        {
            var current = _optionValues.TryGetValue(descriptor.Key, out var chosen) ? chosen : remembered?.GetValueOrDefault(descriptor.Key);
            _optionValues[descriptor.Key] = descriptor.Normalize(current);
            var control = BuildOptionControl(descriptor);
            (descriptor.IsAdvanced ? AdvancedPanel : OptionsPanel).Children.Add(control);
        }

        AdvancedExpander.Visibility = AdvancedPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildOptionControl(OptionDescriptor descriptor)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var value = _optionValues[descriptor.Key];

        FrameworkElement editor;
        switch (descriptor.Kind)
        {
            case OptionKind.Toggle:
                var check = new CheckBox { Content = descriptor.Label, IsChecked = value == "true", ToolTip = descriptor.Description };
                check.Checked += (_, _) => _optionValues[descriptor.Key] = "true";
                check.Unchecked += (_, _) => _optionValues[descriptor.Key] = "false";
                Grid.SetColumn(check, 1);
                grid.Children.Add(check);
                AddDescription(grid, descriptor.Description);
                return grid;
            case OptionKind.Choice:
                var combo = new ComboBox { ItemsSource = descriptor.Choices, DisplayMemberPath = nameof(OptionChoice.Label), SelectedValuePath = nameof(OptionChoice.Value), SelectedValue = value };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is OptionChoice choice)
                    {
                        _optionValues[descriptor.Key] = choice.Value;
                        combo.ToolTip = choice.Description;
                    }
                };
                combo.ToolTip = descriptor.Choices.FirstOrDefault(c => c.Value == value)?.Description;
                editor = combo;
                break;
            default:
                var text = new TextBox { Text = value };
                text.LostFocus += (_, _) =>
                {
                    _optionValues[descriptor.Key] = descriptor.Normalize(text.Text);
                    text.Text = _optionValues[descriptor.Key];
                };
                editor = text;
                break;
        }

        System.Windows.Automation.AutomationProperties.SetName(editor, descriptor.Label);
        var label = new TextBlock { Text = descriptor.Label, Style = (Style)FindResource("FieldLabel") };
        grid.Children.Add(label);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        AddDescription(grid, descriptor.Description);
        return grid;
    }

    private static void AddDescription(Grid grid, string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return;
        }

        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var caption = new TextBlock { Text = description, FontSize = 11.5, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        Grid.SetRow(caption, 1);
        Grid.SetColumn(caption, 1);
        grid.Children.Add(caption);
    }

    private void InitializeDestination()
    {
        var folders = _files.Select(f => _services.DefaultOutputFolder(f, _action)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _folder = folders.Count == 1 ? folders[0] : null;
        FolderBox.Text = _folder ?? "Next to each original file";
        FolderBox.ToolTip = FolderBox.Text;

        var collisionChoices = new List<KeyValuePair<CollisionPolicy, string>>
        {
            new(CollisionPolicy.KeepBoth, "Keep both (add a number to the new file)"),
        };
        if (_action.Kind != ActionKind.Extract)
        {
            collisionChoices.Add(new(CollisionPolicy.Replace, "Replace the existing file"));
        }

        if (IsBatch)
        {
            collisionChoices.Add(new(CollisionPolicy.Skip, "Skip that file"));
        }

        CollisionBox.ItemsSource = collisionChoices;
        CollisionBox.DisplayMemberPath = "Value";
        CollisionBox.SelectedValuePath = "Key";
        CollisionBox.SelectedValue = _services.Settings.DefaultCollisionPolicy == CollisionPolicy.Skip && !IsBatch ? CollisionPolicy.KeepBoth : _services.Settings.DefaultCollisionPolicy;
        if (CollisionBox.SelectedIndex < 0)
        {
            CollisionBox.SelectedIndex = 0;
        }

        if (IsBatch)
        {
            NameRow.Visibility = Visibility.Collapsed;
            ConflictRow.Visibility = Visibility.Visible;
            ConflictText.Text = string.Empty;
            BatchPanel.Visibility = Visibility.Visible;
            foreach (var file in _files)
            {
                // Placeholder rows so the user sees what will be converted before starting.
                var resolved = file.Format == _action.Source ? _action : _services.Registry.Resolve(file.Format, _action.Key) ?? _action;
                var placeholder = new ConversionJob(file, resolved, _services.ChooseEngine(resolved), ConversionOptions.Empty, _folder ?? string.Empty, file.BaseName, CollisionPolicy.KeepBoth);
                _jobs.Add(placeholder);
            }
        }
        else
        {
            NameBox.Text = OutputPlanner.DefaultBaseName(_files[0], _action);
            ExtensionText.Text = _action.Kind == ActionKind.Extract ? "(folder)" : _action.Target.PrimaryExtension;
            UpdateConflict();
        }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => UpdateConflict();

    private void OnCollisionChanged(object sender, SelectionChangedEventArgs e) => UpdateConflict();

    private void UpdateConflict()
    {
        if (IsBatch || _folder is null || NameBox is null || ConflictRow is null)
        {
            return;
        }

        try
        {
            var plan = OutputPlanner.Plan(_files[0], _action, _folder, NameBox.Text);
            SetupError.Visibility = Visibility.Collapsed;
            ConvertButton.IsEnabled = true;
            if (!plan.PrimaryExists)
            {
                ConflictRow.Visibility = Visibility.Collapsed;
                return;
            }

            ConflictRow.Visibility = Visibility.Visible;
            var isSource = PathSafety.IsSamePath(plan.PrimaryPath, _files[0].Path);
            var policy = CollisionBox.SelectedValue is CollisionPolicy p ? p : CollisionPolicy.KeepBoth;
            if (isSource && policy == CollisionPolicy.Replace)
            {
                CollisionBox.SelectedValue = CollisionPolicy.KeepBoth;
                policy = CollisionPolicy.KeepBoth;
            }

            var extension = plan.Shape == OutputShape.Folder ? string.Empty : plan.Extension;
            ConflictText.Text = policy == CollisionPolicy.Replace
                ? $"“{Path.GetFileName(plan.PrimaryPath)}” will be replaced."
                : $"“{Path.GetFileName(plan.PrimaryPath)}” already exists. The new file will be saved as “{Path.GetFileName(OutputPlanner.GetAvailablePath(plan.Directory, plan.BaseName, extension))}”.";
        }
        catch (UnsafePathException ex)
        {
            SetupError.Text = ex.Message;
            SetupError.Visibility = Visibility.Visible;
            ConvertButton.IsEnabled = false;
        }
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to save",
            InitialDirectory = _folder ?? Path.GetDirectoryName(_files[0].Path) ?? string.Empty,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _folder = PathSafety.ValidateOutputDirectory(dialog.FolderName);
            FolderBox.Text = _folder;
            FolderBox.ToolTip = _folder;
            SetupError.Visibility = Visibility.Collapsed;
            UpdateConflict();
        }
        catch (UnsafePathException ex)
        {
            SetupError.Text = ex.Message;
            SetupError.Visibility = Visibility.Visible;
        }
    }

    private void OnConvert(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        var policy = CollisionBox.SelectedValue is CollisionPolicy p ? p : CollisionPolicy.KeepBoth;
        var jobs = new List<ConversionJob>();
        var skipped = new List<string>();
        foreach (var file in _files)
        {
            var action = IsBatch ? _services.Registry.Resolve(file.Format, _action.Key) : _action;
            if (action is null || !action.IsAvailable)
            {
                skipped.Add(file.FileName);
                continue;
            }

            var candidate = _services.ChooseEngine(action, _engine.Engine.Id);
            var folder = _folder ?? _services.DefaultOutputFolder(file, action);
            var baseName = IsBatch ? OutputPlanner.DefaultBaseName(file, action) : PathSafety.SanitizeFileName(NameBox.Text, OutputPlanner.DefaultBaseName(file, action));
            jobs.Add(new ConversionJob(file, action, candidate, ConversionOptions.For(candidate.Capability, _optionValues), folder, baseName, policy));
        }

        if (jobs.Count == 0)
        {
            SetupError.Text = "None of the selected files can be converted with this action.";
            SetupError.Visibility = Visibility.Visible;
            return;
        }

        _started = true;
        _services.RememberChoices(_action, _folder ?? Path.GetDirectoryName(_files[0].Path)!, _optionValues, EngineRow.Visibility == Visibility.Visible ? _engine.Engine.Id : null);

        _jobs.Clear();
        foreach (var job in jobs)
        {
            _jobs.Add(job);
            job.PropertyChanged += OnJobChanged;
        }

        SetupPanel.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        ConvertButton.Visibility = Visibility.Collapsed;
        BackgroundButton.Visibility = Visibility.Visible;
        CancelButton.Content = "Cancel conversion";
        CancelButton.IsCancel = false;
        if (skipped.Count > 0)
        {
            WarningsList.ItemsSource = new[] { $"Skipped (no compatible engine): {string.Join(", ", skipped)}" };
        }

        _services.Queue.EnqueueRange(jobs);
        UpdateStatus();
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateStatus, System.Windows.Threading.DispatcherPriority.Background);

    private void UpdateStatus()
    {
        if (!_started)
        {
            return;
        }

        var finished = _jobs.All(j => j.IsFinished);
        var running = _jobs.FirstOrDefault(j => j.Status == JobStatus.Running);
        var progressValues = _jobs.Select(j => j.IsFinished ? 1 : j.Progress).ToList();
        var known = progressValues.All(v => v is not null);
        Progress.IsIndeterminate = !finished && !known && _jobs.Count == 1;
        Progress.Value = progressValues.Average(v => v ?? 0);
        PercentText.Text = !finished && known ? Progress.Value.ToString("P0", CultureInfo.CurrentCulture) : string.Empty;

        if (!finished)
        {
            StatusGlyph.Text = Glyphs.Refresh;
            StatusGlyph.SetResourceReference(ForegroundProperty, "OrbitAccentBrush");
            StatusText.Text = IsBatch
                ? $"Converting {_jobs.Count(j => j.IsFinished) + 1} of {_jobs.Count}…"
                : running is null ? "Waiting to start…" : $"{_action.Verb}…";
            StageText.Text = running?.Stage ?? (running is null ? "Queued" : string.Empty);
            CancelButton.IsEnabled = _jobs.Any(j => j.CanCancel);
            return;
        }

        ShowResult();
    }

    private void ShowResult()
    {
        Progress.Visibility = Visibility.Collapsed;
        BackgroundButton.Visibility = Visibility.Collapsed;
        CancelButton.Content = "Close";
        CancelButton.IsCancel = true;
        CancelButton.IsEnabled = true;
        PercentText.Text = string.Empty;

        var succeeded = _jobs.Where(j => j.Status == JobStatus.Succeeded).ToList();
        var failed = _jobs.Where(j => j.Status == JobStatus.Failed).ToList();
        var cancelled = _jobs.Where(j => j.Status == JobStatus.Cancelled).ToList();
        var warnings = _jobs.SelectMany(j => j.Warnings.Select(w => IsBatch ? $"{j.Input.FileName}: {w}" : w)).Distinct().ToList();
        if (WarningsList.ItemsSource is IEnumerable<string> existing)
        {
            warnings.InsertRange(0, existing);
        }

        WarningsList.ItemsSource = warnings;

        if (failed.Count == 0 && cancelled.Count == 0)
        {
            StatusGlyph.Text = Glyphs.Check;
            StatusGlyph.SetResourceReference(ForegroundProperty, "OrbitSuccessBrush");
            StatusText.Text = IsBatch ? $"Converted {succeeded.Count} of {_jobs.Count} files" : succeeded.FirstOrDefault()?.Message ?? "Done";
            StageText.Text = IsBatch ? string.Empty : succeeded.FirstOrDefault()?.PrimaryOutput ?? string.Empty;
            var primary = succeeded.FirstOrDefault()?.PrimaryOutput;
            OpenFileButton.Visibility = !IsBatch && primary is not null && File.Exists(primary) ? Visibility.Visible : Visibility.Collapsed;
            OpenFolderButton.Visibility = Visibility.Visible;
            AgainButton.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Collapsed;
            (OpenFileButton.Visibility == Visibility.Visible ? OpenFileButton : OpenFolderButton).Focus();
            return;
        }

        var allCancelled = failed.Count == 0;
        StatusGlyph.Text = allCancelled ? Glyphs.Cancel : Glyphs.Error;
        StatusGlyph.SetResourceReference(ForegroundProperty, allCancelled ? "OrbitSubtleTextBrush" : "OrbitErrorBrush");
        StatusText.Text = IsBatch
            ? $"{succeeded.Count} converted, {failed.Count} failed{(cancelled.Count > 0 ? $", {cancelled.Count} cancelled" : string.Empty)}"
            : (failed.FirstOrDefault() ?? cancelled.First()).Message ?? "The conversion did not complete.";
        StageText.Text = string.Empty;
        RetryButton.Visibility = Visibility.Visible;
        OpenFolderButton.Visibility = succeeded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var diagnostics = string.Join(Environment.NewLine + Environment.NewLine, failed
            .Where(j => j.Diagnostic is not null || IsBatch)
            .Select(j => $"{j.Input.FileName} ({j.Engine.Engine.DisplayName}): {j.Message}{Environment.NewLine}{j.Diagnostic}"));
        DetailsBox.Text = diagnostics;
        DetailsExpander.Visibility = string.IsNullOrWhiteSpace(diagnostics) ? Visibility.Collapsed : Visibility.Visible;
        CopyDetailsButton.Visibility = DetailsExpander.Visibility;

        var needsPassword = failed.FirstOrDefault(j => j.NeedsPassword);
        if (needsPassword is not null)
        {
            PasswordPanel.Visibility = Visibility.Visible;
            PasswordPrompt.Text = needsPassword.Message == "The password is incorrect." ? "The password is incorrect. Try again." : "This file is password protected.";
            RetryButton.Visibility = Visibility.Collapsed;
            PasswordInput.Focus();
        }
    }

    private void Retry(string? password)
    {
        PasswordPanel.Visibility = Visibility.Collapsed;
        DetailsExpander.Visibility = Visibility.Collapsed;
        CopyDetailsButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        Progress.Visibility = Visibility.Visible;
        BackgroundButton.Visibility = Visibility.Visible;
        CancelButton.Content = "Cancel conversion";
        CancelButton.IsCancel = false;
        WarningsList.ItemsSource = null;
        foreach (var job in _jobs.Where(j => j.CanRetry))
        {
            _ = _services.Queue.Retry(job, job.NeedsPassword ? password : null);
        }

        UpdateStatus();
    }

    private void OnRetry(object sender, RoutedEventArgs e) => Retry(null);

    private void OnUnlock(object sender, RoutedEventArgs e)
    {
        var password = PasswordInput.Password;
        PasswordInput.Clear();
        if (password.Length > 0)
        {
            Retry(password);
        }
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnUnlock(sender, e);
            e.Handled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_started && _jobs.Any(j => j.CanCancel))
        {
            foreach (var job in _jobs)
            {
                _services.Queue.Cancel(job);
            }

            StageText.Text = "Cancelling…";
            CancelButton.IsEnabled = false;
            return;
        }

        Close();
    }

    private void OnRunInBackground(object sender, RoutedEventArgs e) => Close();

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var output = _jobs.FirstOrDefault(j => j.Status == JobStatus.Succeeded)?.PrimaryOutput;
        if (output is not null)
        {
            ShellActions.OpenFile(output);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var output = _jobs.FirstOrDefault(j => j.Status == JobStatus.Succeeded)?.PrimaryOutput;
        if (output is not null)
        {
            ShellActions.ShowInFolder(output);
        }
    }

    private void OnConvertAgain(object sender, RoutedEventArgs e)
    {
        var anchor = _anchor;
        Close();
        _convertAgain(_files, anchor);
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{StatusText.Text}{Environment.NewLine}{DetailsBox.Text}");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy; ignore.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var job in _jobs)
        {
            job.PropertyChanged -= OnJobChanged;
        }

        base.OnClosed(e);
    }

    private static string ShortFidelity(Fidelity fidelity) => fidelity switch
    {
        Fidelity.Lossless => "lossless",
        Fidelity.Lossy => "lossy",
        Fidelity.Extraction => "text only",
        Fidelity.LayoutSensitive => "keeps layout",
        Fidelity.Rasterized => "as images",
        _ => "repackage",
    };
}
