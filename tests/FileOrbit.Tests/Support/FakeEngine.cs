using FileOrbit.Core.Capabilities;

namespace FileOrbit.Tests.Support;

/// <summary>Configurable engine for testing the registry, runner and queue without real conversions.</summary>
public sealed class FakeEngine : IConversionEngine
{
    private readonly List<ConversionCapability> _capabilities = [];

    public FakeEngine(string id, bool available = true)
    {
        Id = id;
        Available = available;
    }

    public string Id { get; }

    public string DisplayName => $"Fake {Id}";

    public string Description => "Test engine";

    public bool IsExternal { get; init; }

    public DependencyInfo? Dependency { get; init; }

    public bool Available { get; set; }

    public IReadOnlyList<ConversionCapability> Capabilities => _capabilities;

    /// <summary>What the engine does when asked to convert. Defaults to writing "ok" to the primary output.</summary>
    public Func<ConversionContext, CancellationToken, Task> Behavior { get; set; } = async (context, token) =>
        await File.WriteAllTextAsync(context.PrimaryOutputPath, "ok", token);

    public int Calls { get; private set; }

    public FakeEngine Supports(string source, string target, int quality = 50, ActionKind action = ActionKind.Convert, string? variant = null, OutputShape shape = OutputShape.File)
    {
        _capabilities.Add(new ConversionCapability
        {
            EngineId = Id,
            SourceFormatId = source,
            TargetFormatId = target,
            Quality = quality,
            Action = action,
            Variant = variant,
            Shape = shape,
        });
        return this;
    }

    public EngineStatus GetStatus() => Available ? EngineStatus.Available() : EngineStatus.Unavailable($"{Id} is not installed.");

    public void Refresh()
    {
    }

    public async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        Calls++;
        await Behavior(context, cancellationToken);
    }
}
