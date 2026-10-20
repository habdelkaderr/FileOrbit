using System.Windows;
using System.Windows.Controls;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Radial;

namespace FileOrbit.App.Main;

public sealed record CatalogRow(ConversionAction Action)
{
    public string From => Action.Source.Name;

    public string ActionText => Action.Verb;

    public string Engine => Action.IsAvailable
        ? string.Join(", ", Action.AvailableCandidates.Select(c => c.Engine.DisplayName))
        : Action.Best.Engine.DisplayName;

    public string Fidelity => Action.Best.Capability.Fidelity switch
    {
        Core.Capabilities.Fidelity.Lossless => "Lossless",
        Core.Capabilities.Fidelity.Lossy => "Lossy",
        Core.Capabilities.Fidelity.Extraction => "Content extraction",
        Core.Capabilities.Fidelity.LayoutSensitive => "Layout recreated",
        Core.Capabilities.Fidelity.Rasterized => "Rendered as images",
        _ => "Repackaged",
    };

    public string Status => Action.IsAvailable ? "Available" : Action.MissingDependency is { } dependency ? $"Needs {dependency.Name}" : "Unavailable";

    public string StatusDetail => Action.IsAvailable ? Action.Description : Action.UnavailableReason ?? string.Empty;

    public string SearchText => $"{Action.Source.Id} {Action.Source.Name} {Action.Verb} {Action.Target.Id} {Action.Target.FullName} {Engine} {Status}";
}

public partial class CatalogPage : UserControl
{
    private const string AllCategories = "All categories";
    private readonly AppServices _services;
    private List<CatalogRow> _rows = [];

    public CatalogPage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += (_, _) => Load();
        services.RegistryChanged += (_, _) => Dispatcher.BeginInvoke(Load);
    }

    private void Load()
    {
        _rows = _services.Registry.GetCatalog().Select(a => new CatalogRow(a)).ToList();
        var categories = new List<string> { AllCategories };
        categories.AddRange(_rows.Select(r => RadialMenuBuilder.CategoryTitle(r.Action.Source.Category)).Distinct());
        var selected = CategoryBox.SelectedItem as string;
        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedItem = selected ?? AllCategories;
        var available = _rows.Count(r => r.Action.IsAvailable);
        Summary.Text = $"{available} of {_rows.Count} conversions are available on this PC. Unavailable ones list the software they need; nothing is shown as available unless it works.";
        Apply();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        if (CatalogList is null)
        {
            return;
        }

        var terms = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var category = CategoryBox.SelectedItem as string ?? AllCategories;
        CatalogList.ItemsSource = _rows
            .Where(r => AvailableOnly.IsChecked != true || r.Action.IsAvailable)
            .Where(r => category == AllCategories || RadialMenuBuilder.CategoryTitle(r.Action.Source.Category) == category)
            .Where(r => terms.All(t => r.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
