using FileOrbit.Core.Formats;

namespace FileOrbit.Core.Capabilities;

/// <summary>
/// The single source of truth for what FileOrbit can do. It combines the static capabilities that
/// engines declare with their runtime availability. The UI only ever asks this registry.
/// </summary>
public sealed class CapabilityRegistry
{
    /// <summary>Source format id meaning "any regular file" (e.g. compressing a file into a ZIP).</summary>
    public const string AnyFile = "*";

    private readonly List<IConversionEngine> _engines = [];
    private readonly FormatCatalog _catalog;

    public CapabilityRegistry(FormatCatalog catalog, IEnumerable<IConversionEngine>? engines = null)
    {
        _catalog = catalog;
        foreach (var engine in engines ?? [])
        {
            Register(engine);
        }
    }

    public FormatCatalog Formats => _catalog;

    public IReadOnlyList<IConversionEngine> Engines => _engines;

    public void Register(IConversionEngine engine)
    {
        if (_engines.Any(e => e.Id == engine.Id))
        {
            throw new InvalidOperationException($"An engine with id '{engine.Id}' is already registered.");
        }

        foreach (var capability in engine.Capabilities)
        {
            if (capability.EngineId != engine.Id)
            {
                throw new InvalidOperationException($"Capability {capability.Id} declares engine '{capability.EngineId}' but belongs to '{engine.Id}'.");
            }

            if (capability.SourceFormatId != AnyFile && _catalog.FindById(capability.SourceFormatId) is null)
            {
                throw new InvalidOperationException($"Capability {capability.Id} uses unknown source format '{capability.SourceFormatId}'.");
            }

            if (_catalog.FindById(capability.TargetFormatId) is null)
            {
                throw new InvalidOperationException($"Capability {capability.Id} uses unknown target format '{capability.TargetFormatId}'.");
            }
        }

        _engines.Add(engine);
    }

    public IConversionEngine? FindEngine(string id) => _engines.FirstOrDefault(e => e.Id == id);

    public void RefreshAll()
    {
        foreach (var engine in _engines)
        {
            engine.Refresh();
        }
    }

    /// <summary>All actions for a source format, including unavailable ones (which explain why).</summary>
    public IReadOnlyList<ConversionAction> GetActions(FileFormat source)
    {
        var applicable = _engines
            .SelectMany(engine => engine.Capabilities.Select(capability => (engine, capability)))
            .Where(pair => Applies(pair.capability, source))
            .GroupBy(pair => pair.capability.ActionKey);

        var actions = new List<ConversionAction>();
        foreach (var group in applicable)
        {
            var first = group.First().capability;
            var candidates = group
                .Select(pair => new EngineCandidate(pair.engine, pair.capability, SafeStatus(pair.engine, pair.capability)))
                .OrderByDescending(c => c.Status.IsAvailable)
                .ThenByDescending(c => c.Capability.Quality)
                .ToList();
            actions.Add(new ConversionAction(source, _catalog.GetById(first.TargetFormatId), first.Action, first.Variant, candidates));
        }

        return actions
            .OrderBy(a => _catalog.GetPopularityRank(source.Id, a.Target.Id))
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves one action for a format, or null if no engine declares it.</summary>
    public ConversionAction? Resolve(FileFormat source, string actionKey) =>
        GetActions(source).FirstOrDefault(a => a.Key == actionKey);

    /// <summary>
    /// Actions that apply to every format in a batch. Each file is later resolved against its own
    /// format so a mixed batch can use different engines per file.
    /// </summary>
    public IReadOnlyList<ConversionAction> GetBatchActions(IReadOnlyCollection<FileFormat> sources)
    {
        var distinct = sources.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var perFormat = distinct.Select(GetActions).ToList();
        var commonKeys = perFormat
            .Select(actions => actions.Where(a => a.IsAvailable).Select(a => a.Key).ToHashSet())
            .Aggregate((a, b) => { a.IntersectWith(b); return a; });

        // Unavailable actions are still shown for single-format batches so the user learns what is missing.
        return perFormat[0].Where(a => commonKeys.Contains(a.Key) || (distinct.Count == 1 && !a.IsAvailable)).ToList();
    }

    /// <summary>Every declared conversion, for the searchable catalog.</summary>
    public IReadOnlyList<ConversionAction> GetCatalog()
    {
        var sources = _engines
            .SelectMany(e => e.Capabilities)
            .Select(c => c.SourceFormatId)
            .Where(id => id != AnyFile)
            .Distinct()
            .Select(_catalog.GetById);
        return sources.SelectMany(GetActions)
            .Where(a => a.Best.Capability.SourceFormatId != AnyFile)
            .OrderBy(a => a.Source.Category)
            .ThenBy(a => a.Source.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Applies(ConversionCapability capability, FileFormat source)
    {
        if (capability.SourceFormatId == AnyFile)
        {
            return source != KnownFormats.Folder && source.Category != FormatCategory.Archive && capability.Action == ActionKind.Compress;
        }

        if (!string.Equals(capability.SourceFormatId, source.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A "conversion" to the same format is meaningless; same-format work must be a Transform.
        return capability.Action != ActionKind.Convert || capability.TargetFormatId != source.Id;
    }

    private static CapabilityStatus SafeStatus(IConversionEngine engine, ConversionCapability capability)
    {
        try
        {
            return engine.GetCapabilityStatus(capability);
        }
        catch (Exception ex)
        {
            return CapabilityStatus.Missing($"{engine.DisplayName} could not be checked: {ex.Message}");
        }
    }
}
