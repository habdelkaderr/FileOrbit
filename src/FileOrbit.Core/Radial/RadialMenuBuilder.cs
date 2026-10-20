using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Formats;

namespace FileOrbit.Core.Radial;

public enum RadialItemKind
{
    /// <summary>Runs a conversion action.</summary>
    Action,

    /// <summary>Opens the ring for one category.</summary>
    Category,

    /// <summary>Opens the next page of a crowded ring.</summary>
    More,
}

public sealed record RadialItem(
    string Id,
    RadialItemKind Kind,
    string Label,
    string Tooltip,
    FormatCategory Category,
    ConversionAction? Action = null,
    string? TargetPageId = null,
    bool IsFavorite = false)
{
    public bool IsEnabled => Kind != RadialItemKind.Action || Action!.IsAvailable;

    /// <summary>Secondary text under the label, e.g. "PDF" for category nodes or "Needs FFmpeg".</summary>
    public string? Caption { get; init; }
}

public sealed record RadialPage(string Id, string Title, IReadOnlyList<RadialItem> Items, string? ParentId);

public sealed class RadialMenuModel
{
    public RadialMenuModel(RadialPage root, IReadOnlyDictionary<string, RadialPage> pages)
    {
        Root = root;
        Pages = pages;
    }

    public RadialPage Root { get; }

    public IReadOnlyDictionary<string, RadialPage> Pages { get; }

    public RadialPage GetPage(string id) => Pages[id];
}

public sealed record RadialBuildOptions
{
    /// <summary>How many segments fit around the ring at the current size (computed by the UI).</summary>
    public int MaxSlots { get; init; } = 8;

    public IReadOnlySet<string> Favorites { get; init; } = new HashSet<string>();

    /// <summary>How often each action key was used, to promote habits.</summary>
    public IReadOnlyDictionary<string, int> UsageCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>Show actions whose engine is missing (dimmed, with an explanation).</summary>
    public bool IncludeUnavailable { get; init; } = true;
}

/// <summary>
/// Turns a flat list of actions into rings. The first ring holds the most relevant available
/// actions; everything else stays reachable through category rings and "More" pages, so no ring
/// ever exceeds the number of slots that fit on screen.
/// </summary>
public static class RadialMenuBuilder
{
    public const string RootId = "root";
    public const int MinSlots = 4;
    public const int MaxSlotsLimit = 12;

    public static RadialMenuModel Build(IReadOnlyList<ConversionAction> actions, FormatCatalog catalog, RadialBuildOptions options)
    {
        var slots = Math.Clamp(options.MaxSlots, MinSlots, MaxSlotsLimit);
        var pages = new Dictionary<string, RadialPage>();
        var visible = actions.Where(a => a.IsAvailable || options.IncludeUnavailable).ToList();
        var ranked = Rank(visible, catalog, options);
        var available = ranked.Where(a => a.IsAvailable).ToList();

        if (visible.Count <= slots)
        {
            // Everything fits: no categories needed. Available actions first.
            var items = ranked.OrderByDescending(a => a.IsAvailable).Select(a => ActionItem(a, options)).ToList();
            var root = new RadialPage(RootId, "Convert", items, null);
            pages[RootId] = root;
            return new RadialMenuModel(root, pages);
        }

        var categories = visible
            .GroupBy(a => a.Category)
            .OrderBy(g => g.Min(a => ranked.IndexOf(a)))
            .ToList();

        var categoryItems = new List<RadialItem>();
        foreach (var group in categories)
        {
            var categoryActions = ranked.Where(a => a.Category == group.Key).ToList();
            if (categoryActions.Count == 1)
            {
                categoryItems.Add(ActionItem(categoryActions[0], options));
                continue;
            }

            var pageId = $"category:{group.Key}".ToLowerInvariant();
            AddPagedRing(pages, pageId, CategoryTitle(group.Key), categoryActions.OrderByDescending(a => a.IsAvailable).Select(a => ActionItem(a, options)).ToList(), RootId, slots);
            var availableCount = categoryActions.Count(a => a.IsAvailable);
            categoryItems.Add(new RadialItem(
                pageId,
                RadialItemKind.Category,
                CategoryTitle(group.Key),
                $"{CategoryTitle(group.Key)}: {availableCount} of {categoryActions.Count} available",
                group.Key,
                TargetPageId: pageId)
            {
                Caption = $"{categoryActions.Count} formats",
            });
        }

        List<RadialItem> rootItems;
        var categoryNodes = categoryItems.Where(i => i.Kind == RadialItemKind.Category).ToList();
        if (categoryItems.Count <= slots / 2 + 1)
        {
            // Top actions plus one node per category: everything is one tap away.
            var topCount = slots - categoryItems.Count;
            var shownIds = categoryItems.Where(i => i.Kind == RadialItemKind.Action).Select(i => i.Id).ToHashSet();
            var top = available.Where(a => !shownIds.Contains(a.Key)).Take(topCount).Select(a => ActionItem(a, options));
            rootItems = top.Concat(categoryItems).ToList();
        }
        else
        {
            // Many categories: top actions plus a single "All formats" node leading to a ring of categories.
            const string allId = "categories";
            AddPagedRing(pages, allId, "All formats", categoryItems, RootId, slots);
            var top = available.Take(slots - 1).Select(a => ActionItem(a, options)).ToList();
            top.Add(new RadialItem(allId, RadialItemKind.More, "All formats", "Browse every compatible format by category", FormatCategory.Other, TargetPageId: allId)
            {
                Caption = $"{categoryNodes.Count} groups",
            });
            rootItems = top;
        }

        var rootPage = new RadialPage(RootId, "Convert", rootItems, null);
        pages[RootId] = rootPage;
        return new RadialMenuModel(rootPage, pages);
    }

    /// <summary>
    /// Computes how many segments fit on a ring of the given radius, keeping each segment's arc at
    /// least <paramref name="minArcLength"/> device-independent pixels long.
    /// </summary>
    public static int SlotsForRadius(double ringMidRadius, double minArcLength = 72)
    {
        if (ringMidRadius <= 0 || minArcLength <= 0)
        {
            return MinSlots;
        }

        var slots = (int)Math.Floor(2 * Math.PI * ringMidRadius / minArcLength);
        return Math.Clamp(slots, MinSlots, MaxSlotsLimit);
    }

    public static string CategoryTitle(FormatCategory category) => category switch
    {
        FormatCategory.Document => "Documents",
        FormatCategory.Spreadsheet => "Spreadsheets",
        FormatCategory.Presentation => "Presentations",
        FormatCategory.Image => "Images",
        FormatCategory.Audio => "Audio",
        FormatCategory.Video => "Video",
        FormatCategory.Archive => "Archives",
        FormatCategory.TextData => "Text & data",
        FormatCategory.Folder => "Folder",
        _ => "Other actions",
    };

    private static List<ConversionAction> Rank(List<ConversionAction> actions, FormatCatalog catalog, RadialBuildOptions options) =>
        actions
            .OrderByDescending(a => options.Favorites.Contains(a.Key))
            .ThenByDescending(a => a.IsAvailable)
            .ThenByDescending(a => Math.Min(options.UsageCounts.GetValueOrDefault(a.Key), 20))
            .ThenBy(a => catalog.GetPopularityRank(a.Source.Id, a.Target.Id))
            .ThenBy(a => a.Kind)
            .ThenByDescending(a => a.Best.Capability.Quality)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static RadialItem ActionItem(ConversionAction action, RadialBuildOptions options)
    {
        var tooltip = $"{action.Verb}\n{action.Description}";
        string? caption = null;
        if (!action.IsAvailable)
        {
            caption = action.MissingDependency is { } dependency ? $"Needs {dependency.Name}" : "Unavailable";
            tooltip += $"\nUnavailable: {action.UnavailableReason}";
        }
        else if (action.Kind == ActionKind.Extract)
        {
            caption = "to folder";
        }
        else if (action.Best.Capability.Fidelity is Fidelity.Extraction)
        {
            caption = action.Target.Category == FormatCategory.Spreadsheet || action.Target.Id is "csv" or "tsv" or "json" ? "tables only" : "text only";
        }
        else if (action.Best.Capability.Fidelity is Fidelity.Rasterized && action.Target.Category != FormatCategory.Image)
        {
            caption = "as images";
        }

        return new RadialItem(action.Key, RadialItemKind.Action, action.Title, tooltip, action.Category, action, IsFavorite: options.Favorites.Contains(action.Key))
        {
            Caption = caption,
        };
    }

    /// <summary>Adds a ring, splitting it into chained pages with a "More" item when it is too crowded.</summary>
    private static void AddPagedRing(Dictionary<string, RadialPage> pages, string id, string title, IReadOnlyList<RadialItem> items, string parentId, int slots)
    {
        var pageIndex = 0;
        var remaining = items.ToList();
        var currentId = id;
        var currentParent = parentId;
        while (true)
        {
            if (remaining.Count <= slots)
            {
                pages[currentId] = new RadialPage(currentId, pageIndex == 0 ? title : $"{title} ({pageIndex + 1})", remaining, currentParent);
                return;
            }

            var nextId = $"{id}:page{pageIndex + 2}";
            var pageItems = remaining.Take(slots - 1).ToList();
            remaining = remaining.Skip(slots - 1).ToList();
            pageItems.Add(new RadialItem(nextId, RadialItemKind.More, "More", $"{remaining.Count} more in {title}", FormatCategory.Other, TargetPageId: nextId)
            {
                Caption = $"+{remaining.Count}",
            });
            pages[currentId] = new RadialPage(currentId, pageIndex == 0 ? title : $"{title} ({pageIndex + 1})", pageItems, currentParent);
            currentParent = currentId;
            currentId = nextId;
            pageIndex++;
        }
    }
}
