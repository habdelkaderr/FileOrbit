using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FileOrbit.App.Interop;
using FileOrbit.App.Shell;
using FileOrbit.App.Theming;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Radial;

namespace FileOrbit.App.Radial;

/// <summary>Everything the radial menu needs, decoupled from the rest of the app.</summary>
public sealed class RadialSession
{
    public required IReadOnlyList<DetectedFile> Files { get; init; }

    /// <summary>Builds the menu model for a given number of slots per ring.</summary>
    public required Func<int, RadialMenuModel> BuildModel { get; init; }

    public required Action<ConversionAction> ActionChosen { get; init; }

    /// <summary>The user picked an action whose engine is missing.</summary>
    public required Action<ConversionAction> UnavailableChosen { get; init; }

    /// <summary>Toggles a favorite and returns the new state.</summary>
    public required Func<string, bool> ToggleFavorite { get; init; }

    /// <summary>Screen point (physical pixels) to centre on; the cursor when null.</summary>
    public NativeMethods.NativePoint? Anchor { get; init; }
}

public partial class RadialMenuWindow : Window
{
    private const double OuterRadius = 178;
    private const double InnerRadius = 86;
    private const double HubRadius = 78;
    private const double EdgeMargin = 26;
    private const double MinSegmentArc = 82;

    private readonly RadialSession _session;
    private readonly Stack<RadialPage> _history = new();
    private readonly List<RadialSegment> _segments = [];
    private readonly NativeMethods.NativePoint _anchor;
    private readonly bool _animate = SystemParameters.ClientAreaAnimation;
    private readonly bool _preview;
    private RadialMenuModel _model = null!;
    private RadialPage _page = null!;
    private double _outer;
    private double _inner;
    private double _center;
    private bool _closing;
    private bool _suppressDeactivate;
    private string _idleTitle = string.Empty;
    private string _idleSubtitle = string.Empty;

    public RadialMenuWindow(RadialSession session, bool preview = false)
    {
        _session = session;
        _preview = preview;
        if (preview)
        {
            _animate = false;
        }

        InitializeComponent();

        NativeMethods.GetCursorPos(out var cursor);
        _anchor = session.Anchor ?? cursor;
        var workArea = NativeMethods.GetWorkArea(_anchor, out var dpiScale);

        // Shrink the whole menu on very small screens; fewer slots then fit on the ring.
        var available = Math.Min(workArea.Width, workArea.Height) / dpiScale - 16;
        var natural = 2 * (OuterRadius + EdgeMargin);
        var scale = Math.Min(1, available / natural);
        _outer = OuterRadius * scale;
        _inner = InnerRadius * scale;
        var hub = HubRadius * scale;
        _center = _outer + EdgeMargin * scale;

        Width = Height = 2 * _center;
        Disc.Width = Disc.Height = 2 * _outer;
        Ring.Width = Ring.Height = 2 * _center;
        Hub.Width = Hub.Height = 2 * hub;
        if (scale < 1)
        {
            Hub.LayoutTransform = new ScaleTransform(scale, scale);
        }

        var slots = RadialMenuBuilder.SlotsForRadius((_outer + _inner) / 2, MinSegmentArc);
        _model = session.BuildModel(slots);

        Loaded += OnLoaded;
        if (preview)
        {
            // Off-screen rendering for visual checks: never activates or takes focus.
            ShowActivated = false;
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            Top = -20000;
        }
        else
        {
            SourceInitialized += (_, _) => PlaceAtAnchor();
            DpiChanged += (_, _) => PlaceAtAnchor();
            Deactivated += (_, _) =>
            {
                if (!_suppressDeactivate)
                {
                    CloseAnimated();
                }
            };
        }

        PreviewKeyDown += OnPreviewKeyDown;
        InitializeHub();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_preview)
        {
            ShowPage(_model.Root, direction: 0);
            Root.Opacity = 1;
            RootScale.ScaleX = RootScale.ScaleY = 1;
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
        Activate();
        ShowPage(_model.Root, direction: 0);
        Animate(Root, OpacityProperty, 0, 1, 170);
        Animate(RootScale, ScaleTransform.ScaleXProperty, 0.86, 1, 190);
        Animate(RootScale, ScaleTransform.ScaleYProperty, 0.86, 1, 190);
        if (!_animate)
        {
            Root.Opacity = 1;
        }

        FocusSegment(0);
    }

    /// <summary>Centres the window on the anchor while keeping it fully on the monitor's work area.</summary>
    private void PlaceAtAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var workArea = NativeMethods.GetWorkArea(_anchor, out var dpiScale);
        var size = (int)Math.Ceiling(Width * dpiScale);
        var x = Math.Clamp(_anchor.X - size / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - size));
        var y = Math.Clamp(_anchor.Y - size / 2, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size));
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, size, size, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void InitializeHub()
    {
        var files = _session.Files;
        if (files.Count == 1)
        {
            var file = files[0];
            _idleTitle = file.FileName;
            _idleSubtitle = file.IsDirectory ? "Folder" : $"{file.Format.Name} · {FormatSize(file.SizeBytes)}";
            HubGlyph.Text = Glyphs.ForFormat(file.Format);
            Hub.ToolTip = file.Warning is null ? file.Path : $"{file.Path}\n{file.Warning}";
            _ = LoadThumbnailAsync(file.Path);
        }
        else
        {
            var formats = files.Select(f => f.Format).Distinct().ToList();
            _idleTitle = $"{files.Count} files";
            _idleSubtitle = formats.Count == 1 ? $"{formats[0].Name} · {FormatSize(files.Sum(f => f.SizeBytes))}" : $"{formats.Count} formats · {FormatSize(files.Sum(f => f.SizeBytes))}";
            HubGlyph.Text = Glyphs.Files;
            Hub.ToolTip = string.Join("\n", files.Take(12).Select(f => f.FileName)) + (files.Count > 12 ? $"\n… and {files.Count - 12} more" : string.Empty);
        }

        AutomationProperties.SetName(Hub, $"{_idleTitle}, {_idleSubtitle}");
        RestoreHub();
    }

    private async Task LoadThumbnailAsync(string path)
    {
        var image = await ShellThumbnail.GetAsync(path, 96);
        if (image is not null)
        {
            HubImage.Source = image;
            HubGlyph.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowPage(RadialPage page, int direction)
    {
        _page = page;
        Ring.Children.Clear();
        _segments.Clear();

        var count = page.Items.Count;
        var sweep = 360.0 / Math.Max(1, count);
        var gap = count > 1 ? 1.6 : 0;
        for (var i = 0; i < count; i++)
        {
            var segment = CreateSegment(page.Items[i], i);
            segment.Layout(_center, _inner, _outer, -sweep / 2 + i * sweep, sweep, gap);
            Ring.Children.Add(segment);
            _segments.Add(segment);
        }

        var isRoot = page.ParentId is null;
        HubBackGlyph.Visibility = isRoot ? Visibility.Collapsed : Visibility.Visible;
        Hub.Cursor = isRoot ? Cursors.Arrow : Cursors.Hand;
        AutomationProperties.SetName(Hub, isRoot ? $"{_idleTitle}, {_idleSubtitle}" : $"Back from {page.Title}");
        RestoreHub();

        if (direction != 0 && _animate)
        {
            Animate(Ring, OpacityProperty, 0, 1, 170);
            Animate(RingScale, ScaleTransform.ScaleXProperty, direction > 0 ? 0.9 : 1.08, 1, 190);
            Animate(RingScale, ScaleTransform.ScaleYProperty, direction > 0 ? 0.9 : 1.08, 1, 190);
            Animate(RingRotate, RotateTransform.AngleProperty, direction > 0 ? -14 : 14, 0, 210);
        }
    }

    private RadialSegment CreateSegment(RadialItem item, int index)
    {
        var segment = new RadialSegment(item, index)
        {
            Glyph = Glyphs.ForItem(item),
            Label = item.Label,
            Caption = item.Caption,
            IsUnavailable = item.Kind == RadialItemKind.Action && !item.IsEnabled,
            IsFavorite = item.IsFavorite,
            ToolTip = BuildToolTip(item),
        };
        segment.SetResourceReference(RadialSegment.GlyphBrushProperty, item.Kind == RadialItemKind.More ? "OrbitSubtleTextBrush" : Glyphs.CategoryBrushKey(item.Category));
        segment.Click += (_, _) => Activate(item);
        segment.MouseEnter += (_, _) => ShowHover(item);
        segment.GotKeyboardFocus += (_, _) => ShowHover(item);
        segment.MouseLeave += (_, _) => RestoreHub();

        if (item.Kind == RadialItemKind.Action)
        {
            var menu = new ContextMenu();
            var pin = new MenuItem();
            pin.Click += (_, _) =>
            {
                segment.IsFavorite = _session.ToggleFavorite(item.Action!.Key);
                segment.ToolTip = BuildToolTip(item with { IsFavorite = segment.IsFavorite });
            };
            menu.Items.Add(pin);
            menu.Opened += (_, _) =>
            {
                _suppressDeactivate = true;
                pin.Header = segment.IsFavorite ? "Unpin from first ring" : "Pin to first ring";
            };
            menu.Closed += (_, _) => _suppressDeactivate = false;
            segment.ContextMenu = menu;
        }

        return segment;
    }

    private static ToolTip BuildToolTip(RadialItem item)
    {
        var panel = new StackPanel { MaxWidth = 300 };
        var lines = item.Tooltip.Split('\n');
        panel.Children.Add(new TextBlock { Text = lines[0], FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var line in lines.Skip(1))
        {
            panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.85 });
        }

        if (item.Action is { } action && action.IsAvailable)
        {
            panel.Children.Add(new TextBlock { Text = $"Engine: {action.Best.Engine.DisplayName}", Margin = new Thickness(0, 6, 0, 0), Opacity = 0.65, FontSize = 11 });
        }

        if (item.IsFavorite)
        {
            panel.Children.Add(new TextBlock { Text = "Pinned to the first ring", Margin = new Thickness(0, 4, 0, 0), Opacity = 0.65, FontSize = 11 });
        }

        return new ToolTip { Content = panel };
    }

    private void ShowHover(RadialItem item)
    {
        HubSubtitle.Text = item.Kind switch
        {
            RadialItemKind.Action when !item.IsEnabled => item.Caption ?? "Unavailable",
            RadialItemKind.Action => item.Action!.Verb,
            RadialItemKind.Category => $"Open {item.Label}",
            _ => item.Tooltip,
        };
        HubSubtitle.Foreground = (Brush)FindResource(item.Kind == RadialItemKind.Action && !item.IsEnabled ? "OrbitWarningBrush" : "OrbitAccentBrush");
    }

    private void RestoreHub()
    {
        var isRoot = _page is null || _page.ParentId is null;
        HubTitle.Text = isRoot ? _idleTitle : _page!.Title;
        HubSubtitle.Text = isRoot ? _idleSubtitle : "Back";
        HubSubtitle.SetResourceReference(TextBlock.ForegroundProperty, "OrbitSubtleTextBrush");
    }

    private void Activate(RadialItem item)
    {
        if (_closing)
        {
            return;
        }

        switch (item.Kind)
        {
            case RadialItemKind.Action when item.IsEnabled:
                CloseAnimated(() => _session.ActionChosen(item.Action!));
                break;
            case RadialItemKind.Action:
                CloseAnimated(() => _session.UnavailableChosen(item.Action!));
                break;
            default:
                if (item.TargetPageId is not null && _model.Pages.TryGetValue(item.TargetPageId, out var target))
                {
                    _history.Push(_page);
                    ShowPage(target, direction: 1);
                    FocusSegment(0);
                }

                break;
        }
    }

    /// <summary>For previews and tests: the root grid that holds the rendered menu.</summary>
    internal FrameworkElement Surface => Root;

    internal RadialMenuModel Model => _model;

    /// <summary>For previews and tests: opens a page as if its node had been clicked.</summary>
    internal void OpenPage(string pageId)
    {
        _history.Push(_page);
        ShowPage(_model.Pages[pageId], direction: 0);
    }

    /// <summary>For previews: shows the hover state of the item at <paramref name="index"/>.</summary>
    internal void PreviewHover(int index)
    {
        _segments[index].Focus();
        ShowHover(_segments[index].Item);
    }

    private void GoBack()
    {
        if (_history.Count == 0)
        {
            return;
        }

        var previous = _page;
        var page = _history.Pop();
        ShowPage(page, direction: -1);
        var index = page.Items.ToList().FindIndex(i => i.TargetPageId == previous.Id);
        FocusSegment(Math.Max(0, index));
    }

    private void OnHubClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement as RadialSegment;
        var index = focused?.Index ?? -1;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                if (_history.Count > 0)
                {
                    GoBack();
                }
                else
                {
                    CloseAnimated();
                }

                e.Handled = true;
                break;
            case Key.Back:
                GoBack();
                e.Handled = true;
                break;
            case Key.Right or Key.Down:
                FocusSegment(index + 1);
                e.Handled = true;
                break;
            case Key.Left or Key.Up:
                FocusSegment(index < 0 ? _segments.Count - 1 : index - 1);
                e.Handled = true;
                break;
            case Key.Tab:
                FocusSegment(shift ? (index < 0 ? _segments.Count - 1 : index - 1) : index + 1);
                e.Handled = true;
                break;
            case Key.Enter or Key.Space:
                if (focused is not null)
                {
                    Activate(focused.Item);
                }
                else if (Keyboard.FocusedElement == Hub)
                {
                    GoBack();
                }

                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9:
            case >= Key.NumPad1 and <= Key.NumPad9:
                var number = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
                if (number < _segments.Count)
                {
                    FocusSegment(number);
                    Activate(_segments[number].Item);
                }

                e.Handled = true;
                break;
            case >= Key.A and <= Key.Z:
                var letter = (char)('A' + (e.Key - Key.A));
                for (var step = 1; step <= _segments.Count; step++)
                {
                    var candidate = _segments[(Math.Max(index, -1) + step + _segments.Count) % _segments.Count];
                    if (candidate.Label?.StartsWith(letter.ToString(), StringComparison.OrdinalIgnoreCase) == true)
                    {
                        candidate.Focus();
                        break;
                    }
                }

                e.Handled = true;
                break;
        }
    }

    private void FocusSegment(int index)
    {
        if (_segments.Count == 0)
        {
            Hub.Focus();
            return;
        }

        var wrapped = ((index % _segments.Count) + _segments.Count) % _segments.Count;
        _segments[wrapped].Focus();
    }

    private void CloseAnimated(Action? after = null)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (!_animate || !IsLoaded)
        {
            Close();
            after?.Invoke();
            return;
        }

        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            Close();
            after?.Invoke();
        };
        Animate(RootScale, ScaleTransform.ScaleXProperty, 1, 0.92, 110);
        Animate(RootScale, ScaleTransform.ScaleYProperty, 1, 0.92, 110);
        Root.BeginAnimation(OpacityProperty, fade);
    }

    private void Animate(IAnimatable target, DependencyProperty property, double from, double to, int milliseconds)
    {
        if (!_animate)
        {
            target.BeginAnimation(property, null);
            return;
        }

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        target.BeginAnimation(property, animation);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
