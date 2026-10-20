using System.Reflection;
using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Processes;

namespace FileOrbit.Engines;

/// <summary>A plugin assembly that could not be used, with a user-facing reason.</summary>
public sealed record PluginProblem(string AssemblyName, string Reason);

/// <summary>
/// Loads optional engines from "FileOrbit.Engines.*.dll" assemblies in the application folder.
/// A missing, damaged or policy-blocked plugin never prevents FileOrbit from starting; it is
/// reported on the Engines page instead.
/// </summary>
public static class EnginePlugins
{
    public const string SearchPattern = "FileOrbit.Engines.*.dll";

    public static (IReadOnlyList<IConversionEngine> Engines, IReadOnlyList<PluginProblem> Problems) Load(string directory, ExecutableLocator locator)
    {
        var engines = new List<IConversionEngine>();
        var problems = new List<PluginProblem>();
        if (!Directory.Exists(directory))
        {
            return (engines, problems);
        }

        foreach (var path in Directory.EnumerateFiles(directory, SearchPattern).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var assembly = Assembly.LoadFrom(path);
                foreach (var type in assembly.GetExportedTypes().Where(t => typeof(IConversionEngine).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }))
                {
                    var engine = type.GetConstructor([typeof(ExecutableLocator)]) is { } withLocator
                        ? (IConversionEngine)withLocator.Invoke([locator])
                        : (IConversionEngine)Activator.CreateInstance(type)!;
                    engines.Add(engine);
                }
            }
            catch (FileLoadException ex) when ((uint)ex.HResult == 0x800711C7)
            {
                problems.Add(new PluginProblem(name, "Windows Application Control (for example Smart App Control) blocked this engine plugin. The rest of FileOrbit works normally."));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException or TargetInvocationException or MissingMethodException or TypeLoadException)
            {
                var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                problems.Add(new PluginProblem(name, $"The engine plugin could not be loaded ({cause.GetType().Name}): {cause.Message}"));
            }
        }

        return (engines, problems);
    }
}
