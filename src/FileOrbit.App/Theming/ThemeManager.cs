using System.Windows;
using System.Windows.Media;
using FileOrbit.Core.Settings;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace FileOrbit.App.Theming;

/// <summary>
/// Applies the Fluent theme (light/dark/system) to standard controls and swaps FileOrbit's own
/// palette (used by the radial menu) to match. Follows Windows when set to System.
/// </summary>
public sealed class ThemeManager
{
    private const string PaletteDark = "pack://application:,,,/FileOrbit;component/Theming/PaletteDark.xaml";
    private const string PaletteLight = "pack://application:,,,/FileOrbit;component/Theming/PaletteLight.xaml";
    private readonly Application _application;
    private ResourceDictionary? _palette;
    private ThemePreference _preference = ThemePreference.System;

    public ThemeManager(Application application)
    {
        _application = application;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                _application.Dispatcher.BeginInvoke(() => Apply(_preference));
            }
        };
    }

    public event EventHandler? ThemeChanged;

    public bool IsDark { get; private set; } = true;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        IsDark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => !SystemUsesLightTheme(),
        };

#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental in WPF but is supported for production use.
        _application.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var palette = new ResourceDictionary { Source = new Uri(IsDark ? PaletteDark : PaletteLight) };
        var dictionaries = _application.Resources.MergedDictionaries;
        if (_palette is not null)
        {
            dictionaries.Remove(_palette);
        }

        dictionaries.Add(palette);
        _palette = palette;
        ApplyAccent();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAccent()
    {
        try
        {
            var settings = new UISettings();
            var accent = ToColor(settings.GetColorValue(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1));
            var accentStrong = ToColor(settings.GetColorValue(IsDark ? UIColorType.AccentLight1 : UIColorType.Accent));
            SetBrush("OrbitAccentBrush", accent);
            SetBrush("OrbitAccentStrongBrush", accentStrong);
            SetBrush("OrbitAccentSoftBrush", Color.FromArgb(IsDark ? (byte)0x40 : (byte)0x2A, accentStrong.R, accentStrong.G, accentStrong.B));
            SetBrush("OrbitAccentPressedBrush", Color.FromArgb(IsDark ? (byte)0x66 : (byte)0x44, accentStrong.R, accentStrong.G, accentStrong.B));
        }
        catch (Exception)
        {
            // Keep the palette's default accent if the system accent is unavailable.
        }
    }

    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _palette![key] = brush;
    }

    private static Color ToColor(Windows.UI.Color color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    public static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}
