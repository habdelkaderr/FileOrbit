using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Archives;
using FileOrbit.Engines.Data;
using FileOrbit.Engines.Images;
using FileOrbit.Engines.Media;
using FileOrbit.Engines.Ocr;
using FileOrbit.Engines.Office;
using FileOrbit.Engines.Pdf;
using FileOrbit.Engines.Processes;
using FileOrbit.Engines.Text;

namespace FileOrbit.Engines;

/// <summary>The one place that decides which engines exist. Add new engines here.</summary>
public static class EngineCatalog
{
    /// <summary>Content checks applied to every output of the matching format.</summary>
    public static IReadOnlyList<Core.Jobs.IOutputContentCheck> CreateOutputChecks() => [new PdfOutputCheck()];

    public static IReadOnlyList<IConversionEngine> CreateDefault(IReadOnlyDictionary<string, string>? enginePathOverrides = null) =>
        CreateBuiltIn(new ExecutableLocator(enginePathOverrides));

    /// <summary>Built-in engines plus optional plugin engines found next to the application.</summary>
    public static (IReadOnlyList<IConversionEngine> Engines, IReadOnlyList<PluginProblem> PluginProblems) CreateWithPlugins(
        IReadOnlyDictionary<string, string>? enginePathOverrides, string pluginDirectory)
    {
        var locator = new ExecutableLocator(enginePathOverrides);
        var (plugins, problems) = EnginePlugins.Load(pluginDirectory, locator);
        return ([.. CreateBuiltIn(locator), .. plugins], problems);
    }

    private static IReadOnlyList<IConversionEngine> CreateBuiltIn(ExecutableLocator locator)
    {
        return
        [
            // Built in: always available, run entirely on this PC.
            new ImageEngine(),
            new ImageEditEngine(),
            new BackgroundRemovalEngine(),
            new WindowsPdfEngine(),
            new PdfTextEngine(),
            new TabularEngine(),
            new TextEngine(),
            new DocxTextEngine(),
            new ManagedArchiveEngine(),
            new MediaFoundationEngine(),
            new WindowsOcrEngine(),

            // Detected: used only when the user has installed them.
            new OfficeEngine(),
            new LibreOfficeEngine(locator),
            new FfmpegEngine(locator),
            new SevenZipEngine(locator),

        ];
    }
}
