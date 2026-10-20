namespace FileOrbit.Core.Capabilities;

/// <summary>Convenience base with cached availability checks.</summary>
public abstract class ConversionEngineBase : IConversionEngine
{
    private readonly object _gate = new();
    private EngineStatus? _status;
    private IReadOnlyList<ConversionCapability>? _capabilities;

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract string Description { get; }

    public virtual bool IsExternal => false;

    public virtual DependencyInfo? Dependency => null;

    public IReadOnlyList<ConversionCapability> Capabilities => _capabilities ??= DeclareCapabilities().ToList();

    public EngineStatus GetStatus()
    {
        lock (_gate)
        {
            return _status ??= CheckStatus();
        }
    }

    public virtual void Refresh()
    {
        lock (_gate)
        {
            _status = null;
        }
    }

    public virtual CapabilityStatus GetCapabilityStatus(ConversionCapability capability) => GetStatus().ToCapabilityStatus();

    public abstract Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken);

    protected abstract IEnumerable<ConversionCapability> DeclareCapabilities();

    protected abstract EngineStatus CheckStatus();

    /// <summary>Creates a capability owned by this engine.</summary>
    protected ConversionCapability Cap(string source, string target, Fidelity fidelity, int quality = 50) => new()
    {
        EngineId = Id,
        SourceFormatId = source,
        TargetFormatId = target,
        Fidelity = fidelity,
        Quality = quality,
    };
}
