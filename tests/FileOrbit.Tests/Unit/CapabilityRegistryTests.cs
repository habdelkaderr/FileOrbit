using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Formats;
using FileOrbit.Engines;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Unit;

public sealed class CapabilityRegistryTests
{
    private static readonly FormatCatalog Formats = FormatCatalog.CreateDefault();

    [Fact]
    public void Best_available_engine_wins_over_better_but_missing_engine()
    {
        var missing = new FakeEngine("premium", available: false).Supports("pdf", "docx", quality: 95);
        var builtIn = new FakeEngine("basic").Supports("pdf", "docx", quality: 30);
        var registry = new CapabilityRegistry(Formats, [missing, builtIn]);

        var action = Assert.Single(registry.GetActions(KnownFormats.Pdf));

        Assert.True(action.IsAvailable);
        Assert.Equal("basic", action.Best.Engine.Id);
        Assert.Equal(["basic", "premium"], action.Candidates.Select(c => c.Engine.Id));
    }

    [Fact]
    public void Higher_quality_available_engine_is_preferred()
    {
        var low = new FakeEngine("low").Supports("png", "jpeg", quality: 40);
        var high = new FakeEngine("high").Supports("png", "jpeg", quality: 80);
        var registry = new CapabilityRegistry(Formats, [low, high]);

        Assert.Equal("high", registry.GetActions(KnownFormats.Png).Single().Best.Engine.Id);
    }

    [Fact]
    public void Unavailable_action_explains_why_and_names_the_dependency()
    {
        var dependency = new DependencyInfo("FFmpeg", "video", "LGPL", "https://ffmpeg.org", "Gyan.FFmpeg");
        var engine = new FakeEngine("ffmpeg", available: false) { Dependency = dependency }.Supports("mkv", "mp4");
        var registry = new CapabilityRegistry(Formats, [engine]);

        var action = registry.GetActions(KnownFormats.Mkv).Single();

        Assert.False(action.IsAvailable);
        Assert.Equal("ffmpeg is not installed.", action.UnavailableReason);
        Assert.Equal(dependency, action.MissingDependency);
    }

    [Fact]
    public void Same_format_convert_is_never_offered()
    {
        var engine = new FakeEngine("e").Supports("png", "png");
        var registry = new CapabilityRegistry(Formats, [engine]);
        Assert.Empty(registry.GetActions(KnownFormats.Png));
    }

    [Fact]
    public void Same_format_transform_is_offered()
    {
        var engine = new FakeEngine("e").Supports("png", "png", action: ActionKind.Transform, variant: "resized");
        var registry = new CapabilityRegistry(Formats, [engine]);
        var action = Assert.Single(registry.GetActions(KnownFormats.Png));
        Assert.Equal("transform:png:resized", action.Key);
    }

    [Fact]
    public void Any_file_compress_applies_to_files_but_not_folders_or_archives()
    {
        var engine = new FakeEngine("zip").Supports(CapabilityRegistry.AnyFile, "zip", action: ActionKind.Compress);
        var registry = new CapabilityRegistry(Formats, [engine]);

        Assert.Single(registry.GetActions(KnownFormats.Pdf));
        Assert.Empty(registry.GetActions(KnownFormats.Folder));
        Assert.Empty(registry.GetActions(KnownFormats.SevenZip));
    }

    [Fact]
    public void Duplicate_engine_ids_are_rejected()
    {
        var registry = new CapabilityRegistry(Formats, [new FakeEngine("x")]);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeEngine("x")));
    }

    [Fact]
    public void Unknown_formats_in_capabilities_are_rejected()
    {
        var registry = new CapabilityRegistry(Formats);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeEngine("x").Supports("pdf", "nope")));
    }

    [Fact]
    public void Registry_is_extensible_with_new_formats_and_engines()
    {
        var catalog = FormatCatalog.CreateDefault();
        var dwg = new FileFormat("dwg", "DWG", "AutoCAD drawing", FormatCategory.Document, "image/vnd.dwg", ".dwg");
        catalog.Register(dwg);
        var registry = new CapabilityRegistry(catalog, [new FakeEngine("cad").Supports("dwg", "pdf")]);

        var action = Assert.Single(registry.GetActions(dwg));
        Assert.Equal("convert:pdf", action.Key);
        Assert.Equal(dwg, catalog.FindByFileName("plan.DWG"));
    }

    [Fact]
    public void Batch_actions_are_the_available_intersection()
    {
        var engine = new FakeEngine("img")
            .Supports("png", "jpeg").Supports("png", "webp")
            .Supports("bmp", "jpeg").Supports("bmp", "gif");
        var registry = new CapabilityRegistry(Formats, [engine]);

        var actions = registry.GetBatchActions([KnownFormats.Png, KnownFormats.Bmp]);

        Assert.Equal(["convert:jpeg"], actions.Select(a => a.Key));
    }

    [Fact]
    public void Engine_status_exceptions_become_unavailable_reasons()
    {
        var registry = new CapabilityRegistry(Formats, [new ThrowingEngine()]);
        var action = registry.GetActions(KnownFormats.Pdf).Single();
        Assert.False(action.IsAvailable);
        Assert.Contains("could not be checked", action.UnavailableReason);
    }

    [Fact]
    public void Real_catalog_registers_without_errors_and_never_lists_unknown_formats()
    {
        var registry = new CapabilityRegistry(Formats, EngineCatalog.CreateDefault());
        var catalog = registry.GetCatalog();

        Assert.NotEmpty(catalog);
        Assert.All(catalog, a => Assert.NotNull(Formats.FindById(a.Target.Id)));
        // Built-in engines guarantee at least these core conversions on any Windows 10/11 PC.
        foreach (var (source, key) in new[] { ("pdf", "convert:png"), ("pdf", "convert:txt"), ("png", "convert:jpeg"), ("csv", "convert:xlsx"), ("zip", "extract:folder"), ("wav", "convert:mp3") })
        {
            Assert.True(registry.Resolve(Formats.GetById(source), key)?.IsAvailable, $"{source} {key} should be available");
        }
    }

    [Fact]
    public void Options_are_normalised_against_their_descriptors()
    {
        var capability = new ConversionCapability
        {
            EngineId = "e",
            SourceFormatId = "png",
            TargetFormatId = "jpeg",
            Options =
            [
                OptionDescriptor.Choice("quality", "Quality", "90", new("90", "High"), new("60", "Low")),
                OptionDescriptor.Integer("width", "Width", 100, 1, 1000),
                OptionDescriptor.Toggle("strip", "Strip", false),
            ],
        };

        var options = ConversionOptions.For(capability, new Dictionary<string, string> { ["quality"] = "13", ["width"] = "99999", ["strip"] = "yes", ["unknown"] = "x" });

        Assert.Equal("90", options.Get("quality", ""));
        Assert.Equal(1000, options.GetInt("width", 0));
        Assert.False(options.GetBool("strip", true));
        Assert.False(options.Values.ContainsKey("unknown"));
    }

    private sealed class ThrowingEngine : ConversionEngineBase
    {
        public override string Id => "throws";

        public override string DisplayName => "Throws";

        public override string Description => string.Empty;

        public override CapabilityStatus GetCapabilityStatus(ConversionCapability capability) => throw new InvalidOperationException("boom");

        public override Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        protected override IEnumerable<ConversionCapability> DeclareCapabilities() => [Cap("pdf", "txt", Fidelity.Extraction)];

        protected override EngineStatus CheckStatus() => EngineStatus.Available();
    }
}

public sealed class EnginePluginTests
{
    [Fact]
    public void Broken_plugin_is_reported_and_does_not_stop_loading()
    {
        using var dir = new TestDirectory();
        dir.Write("FileOrbit.Engines.Broken.dll", [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        dir.Write("Unrelated.dll", [0x00]);

        var (engines, problems) = EnginePlugins.Load(dir.Path, new FileOrbit.Engines.Processes.ExecutableLocator());

        Assert.Empty(engines);
        var problem = Assert.Single(problems);
        Assert.Equal("FileOrbit.Engines.Broken", problem.AssemblyName);
    }

    [Fact]
    public void Missing_plugin_folder_is_fine()
    {
        var (engines, problems) = EnginePlugins.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), new FileOrbit.Engines.Processes.ExecutableLocator());
        Assert.Empty(engines);
        Assert.Empty(problems);
    }
}
