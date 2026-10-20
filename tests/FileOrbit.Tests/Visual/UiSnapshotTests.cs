using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileOrbit.App;
using FileOrbit.App.Conversion;
using FileOrbit.App.Main;
using FileOrbit.App.Radial;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Jobs;
using FileOrbit.Core.Radial;
using FileOrbit.Core.Settings;
using FileOrbit.Tests.Support;
using Application = System.Windows.Application;

namespace FileOrbit.Tests.Visual;

/// <summary>
/// Renders the real windows off-screen (never shown, never activated) into PNG files for visual
/// review. Opt-in: set FILEORBIT_SNAPSHOT_DIR to an output folder.
/// </summary>
public sealed class UiSnapshotTests
{
    private static readonly string? OutputDirectory = Environment.GetEnvironmentVariable("FILEORBIT_SNAPSHOT_DIR");

    [Fact]
    public async Task Render_ui_snapshots()
    {
        if (string.IsNullOrEmpty(OutputDirectory))
        {
            Assert.Skip("Set FILEORBIT_SNAPSHOT_DIR to render UI snapshots.");
        }

        Directory.CreateDirectory(OutputDirectory);
        using var dir = new TestDirectory();
        var pdf = SampleFiles.Pdf(dir, "Financial_Report.pdf", pages: 2);
        var png = await SampleFiles.PngAsync(dir, "Orbit logo.png", 256, 256);
        var csv = SampleFiles.Csv(dir, "Customers.csv");
        var wav = SampleFiles.Wav(dir, "Interview.wav");
        var data = dir.SubDirectory("appdata");

        WpfHost.Run(_ => RenderAll(data, pdf, png, csv, wav));
    }

    private static void RenderAll(string dataDirectory, string pdf, string png, string csv, string wav)
    {
        var app = Application.Current;
        var theme = new FileOrbit.App.Theming.ThemeManager(app);
        var services = new AppServices(dataDirectory);
        services.History.Add(new HistoryEntry(DateTimeOffset.Now.AddMinutes(-5), pdf, "pdf", "convert:docx", "Convert to DOCX", "ms-office",
            JobStatus.Succeeded, [Path.ChangeExtension(pdf, ".docx")], "Saved Financial_Report.docx", 3.2));

        foreach (var (name, preference) in new[] { ("dark", ThemePreference.Dark), ("light", ThemePreference.Light) })
        {
            theme.Apply(preference);
            var background = preference == ThemePreference.Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);

            var pdfFile = services.Detector.Detect(pdf);
            RenderRadial(services, [pdfFile], $"radial-pdf-{name}", background, page: null, hover: 0);
            var model = services.BuildRadialModel([pdfFile], RadialMenuBuilder.SlotsForRadius(132, 82));
            var firstCategory = model.Root.Items.FirstOrDefault(i => i.TargetPageId is not null);
            if (firstCategory is not null)
            {
                RenderRadial(services, [pdfFile], $"radial-pdf-{firstCategory.TargetPageId!.Replace(':', '-')}-{name}", background, firstCategory.TargetPageId, hover: null);
            }

            RenderRadial(services, [services.Detector.Detect(png)], $"radial-png-{name}", background, null, null);
            if (name == "dark")
            {
                RenderRadial(services, [services.Detector.Detect(wav)], "radial-wav-dark", background, null, null);
                RenderRadial(services, [services.Detector.Detect(csv)], "radial-csv-dark", background, null, null);
                RenderRadial(services, [services.Detector.Detect(png), services.Detector.Detect(pdf)], "radial-batch-dark", background, null, null);
            }

            var docx = services.Registry.Resolve(pdfFile.Format, "convert:docx")!;
            RenderWindow(new ConversionWindow(services, [pdfFile], docx, null, (_, _) => { }, preview: true), $"card-pdf-docx-{name}", background);
            var pngAction = services.Registry.Resolve(pdfFile.Format, "convert:png")!;
            RenderWindow(new ConversionWindow(services, [pdfFile], pngAction, null, (_, _) => { }, preview: true), $"card-pdf-png-{name}", background);

            var main = new MainWindow(services) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            foreach (var page in Enum.GetValues<MainPage>())
            {
                main.Navigate(page);
                RenderWindow(main, $"main-{page.ToString().ToLowerInvariant()}-{name}", background, close: false);
            }

            main.Close();
        }

        services.Dispose();
    }

    private static void RenderRadial(AppServices services, IReadOnlyList<DetectedFile> files, string name, Color background, string? page, int? hover)
    {
        var window = new RadialMenuWindow(new RadialSession
        {
            Files = files,
            BuildModel = slots => services.BuildRadialModel(files, slots),
            ActionChosen = _ => { },
            UnavailableChosen = _ => { },
            ToggleFavorite = _ => false,
        }, preview: true);
        window.Show();
        Pump(TimeSpan.FromMilliseconds(900));
        if (page is not null)
        {
            window.OpenPage(page);
        }

        if (hover is not null)
        {
            window.PreviewHover(hover.Value);
        }

        Pump(TimeSpan.FromMilliseconds(200));
        Save(window.Surface, window.Width, window.Height, name, background);
        window.Close();
    }

    private static void RenderWindow(Window window, string name, Color background, bool close = true)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        Pump(TimeSpan.FromMilliseconds(700));
        // The window's root visual includes the content's margins.
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        Save(root, root.ActualWidth, root.ActualHeight, name, background);
        if (close)
        {
            window.Close();
        }
    }

    private static void Save(FrameworkElement element, double width, double height, string name, Color background)
    {
        const double scale = 1.5;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(OutputDirectory!, name + ".png"));
        encoder.Save(stream);
    }

    private static void Pump(TimeSpan duration) => WpfHost.Pump(duration);
}
