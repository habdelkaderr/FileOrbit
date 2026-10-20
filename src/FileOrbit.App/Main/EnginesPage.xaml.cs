using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileOrbit.App.Shell;
using FileOrbit.App.Theming;
using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Processes;
using Microsoft.Win32;

namespace FileOrbit.App.Main;

public sealed class EngineRow
{
    public EngineRow(IConversionEngine engine, int availableActions, int totalActions)
    {
        Engine = engine;
        var status = engine.GetStatus();
        IsAvailable = status.IsAvailable;
        Status = status.IsAvailable
            ? $"{status.Summary}{(status.Version is null ? string.Empty : $" Â· version {status.Version}")} Â· {availableActions} of {totalActions} conversions"
            : $"{status.Summary} {totalActions} conversions need it.";
        Details = string.Join("  Â·  ", new[]
        {
            status.Location is null ? null : $"Location: {status.Location}",
            engine.Dependency is null ? null : $"Provides: {engine.Dependency.Purpose}",
            engine.Dependency is null ? null : $"Licence: {engine.Dependency.License}",
            engine.Dependency?.InstallNotes,
        }.Where(s => s is not null));
    }

    public IConversionEngine Engine { get; }

    public bool IsAvailable { get; }

    public string Name => Engine.DisplayName;

    public string Kind => Engine.IsExternal ? "Optional" : "Built in";

    public string Description => Engine.Description;

    public string Status { get; }

    public string Details { get; }

    public string StatusGlyph => IsAvailable ? Glyphs.Check : Glyphs.Warning;

    public Brush StatusBrush => (Brush)Application.Current.FindResource(IsAvailable ? "OrbitSuccessBrush" : "OrbitWarningBrush");

    public bool CanInstall => !IsAvailable && Engine.Dependency?.WingetId is not null;

    public bool CanLocate => Engine.Id is "7zip" or "ffmpeg" or "libreoffice" or "ghostscript";

    public bool HasWebsite => Engine.Dependency?.Website is not null;
}

public partial class EnginesPage : UserControl
{
    private readonly AppServices _services;

    public EnginesPage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += (_, _) => Load();
        services.RegistryChanged += (_, _) => Dispatcher.BeginInvoke(Load);
    }

    private void Load()
    {
        var registry = _services.Registry;
        var catalog = registry.GetCatalog();
        EngineList.ItemsSource = registry.Engines.Select(engine =>
        {
            var mine = catalog.SelectMany(a => a.Candidates).Where(c => c.Engine == engine).ToList();
            return new EngineRow(engine, mine.Count(c => c.Status.IsAvailable), mine.Count);
        })
        .OrderBy(r => r.Engine.IsExternal)
        .ThenBy(r => r.Name)
        .ToList();
        PluginProblems.ItemsSource = _services.PluginProblems;
        PluginProblems.Visibility = _services.PluginProblems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _services.RefreshEngines();

    private void OnWebsite(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is EngineRow { Engine.Dependency: { } dependency })
        {
            ShellActions.OpenUrl(dependency.Website);
        }
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not EngineRow { Engine.Dependency: { WingetId: { } wingetId } dependency })
        {
            return;
        }

        var winget = ExecutableLocator.FindOnPath("winget.exe")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        var command = $"winget install --id {wingetId} --exact --source winget";
        var message = $"FileOrbit will open a terminal window and run:\n\n    {command}\n\n" +
                      $"{dependency.Name} is licensed under {dependency.License}. Windows Package Manager will show the publisher's licence terms. " +
                      "You can watch and cancel the installation in the terminal.\n\nContinue?";
        if (MessageBox.Show(Window.GetWindow(this), message, $"Install {dependency.Name}", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            // A visible console so the user sees exactly what is installed; never silent.
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { UseShellExecute = true };
            info.ArgumentList.Add("/k");
            info.ArgumentList.Add(File.Exists(winget) ? winget : "winget");
            foreach (var part in new[] { "install", "--id", wingetId, "--exact", "--source", "winget" })
            {
                info.ArgumentList.Add(part);
            }

            Process.Start(info)?.Dispose();
            MessageBox.Show(Window.GetWindow(this), "When the installation finishes, press â€œCheck againâ€.", "FileOrbit", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"Could not start Windows Package Manager: {ex.Message}\n\nInstall {dependency.Name} from {dependency.Website}.", "FileOrbit");
        }
    }

    private void OnLocate(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not EngineRow row)
        {
            return;
        }

        var exeName = row.Engine.Id switch
        {
            "ffmpeg" => "ffmpeg.exe",
            "7zip" => "7z.exe",
            "ghostscript" => "gswin64c.exe",
            _ => "soffice.exe",
        };
        var dialog = new OpenFileDialog { Title = $"Locate {exeName}", Filter = $"{exeName}|{exeName}", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && ExecutableLocator.IsValidExecutable(dialog.FileName))
        {
            _services.Settings.EnginePaths[row.Engine.Id] = dialog.FileName;
            _services.SaveSettings();
            _services.RefreshEngines();
        }
    }
}
