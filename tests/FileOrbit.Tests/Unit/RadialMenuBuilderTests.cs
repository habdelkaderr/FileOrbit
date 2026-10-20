using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Formats;
using FileOrbit.Core.Radial;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Unit;

public sealed class RadialMenuBuilderTests
{
    private static readonly FormatCatalog Formats = FormatCatalog.CreateDefault();

    private static IReadOnlyList<ConversionAction> Actions(FileFormat source, params string[] targets)
    {
        var engine = new FakeEngine("e");
        foreach (var target in targets)
        {
            engine.Supports(source.Id, target);
        }

        return new CapabilityRegistry(Formats, [engine]).GetActions(source);
    }

    [Fact]
    public void Few_actions_are_shown_directly_without_categories()
    {
        var model = RadialMenuBuilder.Build(Actions(KnownFormats.Png, "jpeg", "webp", "bmp"), Formats, new RadialBuildOptions { MaxSlots = 8 });

        Assert.All(model.Root.Items, i => Assert.Equal(RadialItemKind.Action, i.Kind));
        Assert.Equal(3, model.Root.Items.Count);
        Assert.Equal("convert:jpeg", model.Root.Items[0].Id); // Most popular target first.
    }

    [Fact]
    public void Many_actions_never_exceed_slots_on_any_page()
    {
        var targets = new[] { "docx", "png", "jpeg", "txt", "xlsx", "pptx", "html", "tiff", "md", "csv", "rtf", "odt", "bmp", "gif", "webp" };
        foreach (var slots in new[] { 4, 6, 8, 10, 12 })
        {
            var model = RadialMenuBuilder.Build(Actions(KnownFormats.Pdf, targets), Formats, new RadialBuildOptions { MaxSlots = slots });
            Assert.All(model.Pages.Values, page => Assert.InRange(page.Items.Count, 1, slots));
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void Every_action_is_reachable(int slots)
    {
        var targets = new[] { "docx", "png", "jpeg", "txt", "xlsx", "pptx", "html", "tiff", "md", "csv", "rtf", "odt", "bmp", "gif", "webp" };
        var actions = Actions(KnownFormats.Pdf, targets);
        var model = RadialMenuBuilder.Build(actions, Formats, new RadialBuildOptions { MaxSlots = slots });

        var reachable = new HashSet<string>();
        var queue = new Queue<RadialPage>([model.Root]);
        var visited = new HashSet<string>();
        while (queue.Count > 0)
        {
            var page = queue.Dequeue();
            if (!visited.Add(page.Id))
            {
                continue;
            }

            foreach (var item in page.Items)
            {
                if (item.Kind == RadialItemKind.Action)
                {
                    reachable.Add(item.Id);
                }
                else
                {
                    queue.Enqueue(model.GetPage(item.TargetPageId!));
                }
            }
        }

        Assert.Equal(actions.Select(a => a.Key).ToHashSet(), reachable);
    }

    [Fact]
    public void Favorites_are_promoted_to_the_first_ring()
    {
        var targets = new[] { "docx", "png", "jpeg", "txt", "xlsx", "pptx", "html", "tiff", "md", "csv", "rtf", "odt", "bmp", "gif", "webp" };
        var model = RadialMenuBuilder.Build(Actions(KnownFormats.Pdf, targets), Formats, new RadialBuildOptions
        {
            MaxSlots = 8,
            Favorites = new HashSet<string> { "convert:gif" },
        });

        var first = model.Root.Items.First(i => i.Kind == RadialItemKind.Action);
        Assert.Equal("convert:gif", first.Id);
        Assert.True(first.IsFavorite);
    }

    [Fact]
    public void Usage_history_reorders_actions()
    {
        var model = RadialMenuBuilder.Build(Actions(KnownFormats.Png, "jpeg", "webp", "bmp"), Formats, new RadialBuildOptions
        {
            UsageCounts = new Dictionary<string, int> { ["convert:bmp"] = 7 },
        });

        Assert.Equal("convert:bmp", model.Root.Items[0].Id);
    }

    [Fact]
    public void Unavailable_actions_are_marked_and_can_be_hidden()
    {
        var missing = new FakeEngine("ffmpeg", available: false) { Dependency = new DependencyInfo("FFmpeg", "x", "LGPL", "https://ffmpeg.org") }.Supports("wav", "ogg");
        var ok = new FakeEngine("mf").Supports("wav", "mp3");
        var actions = new CapabilityRegistry(Formats, [missing, ok]).GetActions(KnownFormats.Wav);

        var shown = RadialMenuBuilder.Build(actions, Formats, new RadialBuildOptions());
        var unavailable = shown.Root.Items.Single(i => i.Id == "convert:ogg");
        Assert.False(unavailable.IsEnabled);
        Assert.Equal("Needs FFmpeg", unavailable.Caption);
        Assert.Equal("convert:mp3", shown.Root.Items[0].Id);

        var hidden = RadialMenuBuilder.Build(actions, Formats, new RadialBuildOptions { IncludeUnavailable = false });
        Assert.DoesNotContain(hidden.Root.Items, i => i.Id == "convert:ogg");
    }

    [Theory]
    [InlineData(0, RadialMenuBuilder.MinSlots)]
    [InlineData(40, RadialMenuBuilder.MinSlots)]
    [InlineData(132, 10)]
    [InlineData(400, RadialMenuBuilder.MaxSlotsLimit)]
    public void Slots_adapt_to_ring_size(double radius, int expected) =>
        Assert.Equal(expected, RadialMenuBuilder.SlotsForRadius(radius, 82));
}
