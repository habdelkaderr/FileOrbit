using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using FileOrbit.App;
using FileOrbit.App.Conversion;
using FileOrbit.App.Radial;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Jobs;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Visual;

/// <summary>
/// Drives the real radial menu and conversion card off-screen through UI Automation, the same
/// interface screen readers use, and checks that a real file is produced.
/// </summary>
public sealed class UiFlowTests
{
    [Fact]
    public void Radial_segment_to_conversion_card_to_converted_file()
    {
        using var dir = new TestDirectory();
        var csv = SampleFiles.Csv(dir, "Customers.csv");
        var data = dir.SubDirectory("appdata");

        WpfHost.Run(_ =>
        {
            var theme = new FileOrbit.App.Theming.ThemeManager(System.Windows.Application.Current);
            theme.Apply(FileOrbit.Core.Settings.ThemePreference.Dark);
            using var services = new AppServices(data);
            var file = services.Detector.Detect(csv);

            // 1. The radial menu offers JSON for a CSV; invoking that segment reports the action.
            ConversionAction? chosen = null;
            var radial = new RadialMenuWindow(new RadialSession
            {
                Files = [file],
                BuildModel = slots => services.BuildRadialModel([file], slots),
                ActionChosen = action => chosen = action,
                UnavailableChosen = _ => throw new InvalidOperationException("JSON should be available"),
                ToggleFavorite = _ => false,
            }, preview: true);
            radial.Show();
            WpfHost.Pump(TimeSpan.FromMilliseconds(500));

            var segment = FindSegment(radial, "convert:json");
            Assert.NotNull(segment);
            Assert.Equal(AutomationControlType.Button, UIElementAutomationPeer.CreatePeerForElement(segment).GetAutomationControlType());
            Invoke(segment);
            WpfHost.Pump(TimeSpan.FromMilliseconds(200), () => chosen is not null);
            Assert.Equal("convert:json", chosen?.Key);

            // 2. The card converts it for real.
            var card = new ConversionWindow(services, [file], chosen!, null, (_, _) => { }, preview: true);
            card.Show();
            WpfHost.Pump(TimeSpan.FromMilliseconds(300));
            Assert.Equal("Customers", card.NameBox.Text);
            Invoke(card.ConvertButton);
            WpfHost.Pump(TimeSpan.FromSeconds(20), () => card.Jobs.Count > 0 && card.Jobs.All(j => j.IsFinished));
            WpfHost.Pump(TimeSpan.FromMilliseconds(300));

            var job = Assert.Single(card.Jobs);
            Assert.Equal(JobStatus.Succeeded, job.Status);
            Assert.Equal(dir.File("Customers.json"), job.PrimaryOutput);
            Assert.Contains("Saved Customers.json", card.StatusText.Text);
            Assert.Equal(System.Windows.Visibility.Visible, card.OpenFolderButton.Visibility);
            card.Close();
        });

        Assert.True(File.Exists(dir.File("Customers.json")));
    }

    [Fact]
    public void Unavailable_segment_routes_to_dependency_help()
    {
        using var dir = new TestDirectory();
        var wav = SampleFiles.Wav(dir);
        var data = dir.SubDirectory("appdata");

        WpfHost.Run(_ =>
        {
            using var services = new AppServices(data);
            if (services.Registry.FindEngine("ffmpeg")?.GetStatus().IsAvailable == true)
            {
                return; // FFmpeg installed: nothing is unavailable for this test to check.
            }

            var file = services.Detector.Detect(wav);
            ConversionAction? unavailable = null;
            var radial = new RadialMenuWindow(new RadialSession
            {
                Files = [file],
                BuildModel = slots => services.BuildRadialModel([file], slots),
                ActionChosen = _ => throw new InvalidOperationException("OGG must not start without FFmpeg"),
                UnavailableChosen = action => unavailable = action,
                ToggleFavorite = _ => false,
            }, preview: true);
            radial.Show();
            WpfHost.Pump(TimeSpan.FromMilliseconds(500));

            var segment = FindSegment(radial, "convert:ogg");
            Assert.NotNull(segment);
            Assert.True(segment.IsUnavailable);
            Invoke(segment);
            WpfHost.Pump(TimeSpan.FromMilliseconds(200), () => unavailable is not null);
            Assert.Equal("FFmpeg", unavailable?.MissingDependency?.Name);
        });
    }

    private static RadialSegment? FindSegment(RadialMenuWindow radial, string actionKey)
    {
        // The action may sit on the root ring or inside a category ring.
        foreach (var page in radial.Model.Pages.Values.OrderBy(p => p.ParentId is null ? 0 : 1))
        {
            if (page.Items.Any(i => i.Id == actionKey))
            {
                if (page.ParentId is not null)
                {
                    radial.OpenPage(page.Id);
                    WpfHost.Pump(TimeSpan.FromMilliseconds(100));
                }

                return FindVisual<RadialSegment>(radial.Surface).FirstOrDefault(s => s.Item.Id == actionKey);
            }
        }

        return null;
    }

    private static void Invoke(System.Windows.UIElement element)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        var invoke = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!;
        invoke.Invoke();
    }

    private static IEnumerable<T> FindVisual<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisual<T>(child))
            {
                yield return nested;
            }
        }
    }
}
