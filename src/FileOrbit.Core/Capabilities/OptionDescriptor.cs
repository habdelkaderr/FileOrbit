using System.Globalization;

namespace FileOrbit.Core.Capabilities;

public enum OptionKind
{
    Choice,
    Integer,
    Toggle,
    Text,
}

public sealed record OptionChoice(string Value, string Label, string? Description = null);

/// <summary>Describes a conversion setting so the UI can render it without knowing the engine.</summary>
public sealed record OptionDescriptor
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public OptionKind Kind { get; init; } = OptionKind.Choice;

    public string DefaultValue { get; init; } = string.Empty;

    public IReadOnlyList<OptionChoice> Choices { get; init; } = [];

    public int Minimum { get; init; }

    public int Maximum { get; init; } = int.MaxValue;

    public string? Description { get; init; }

    /// <summary>Advanced options are collapsed by default.</summary>
    public bool IsAdvanced { get; init; }

    public static OptionDescriptor Choice(string key, string label, string defaultValue, params OptionChoice[] choices) =>
        new() { Key = key, Label = label, Kind = OptionKind.Choice, DefaultValue = defaultValue, Choices = choices };

    public static OptionDescriptor Integer(string key, string label, int defaultValue, int min, int max, string? description = null) =>
        new() { Key = key, Label = label, Kind = OptionKind.Integer, DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture), Minimum = min, Maximum = max, Description = description };

    public static OptionDescriptor Toggle(string key, string label, bool defaultValue, string? description = null) =>
        new() { Key = key, Label = label, Kind = OptionKind.Toggle, DefaultValue = defaultValue ? "true" : "false", Description = description };

    /// <summary>Returns a valid value for this option, falling back to the default.</summary>
    public string Normalize(string? value)
    {
        if (value is null)
        {
            return DefaultValue;
        }

        switch (Kind)
        {
            case OptionKind.Choice:
                return Choices.Any(c => c.Value == value) ? value : DefaultValue;
            case OptionKind.Integer:
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    ? Math.Clamp(number, Minimum, Maximum).ToString(CultureInfo.InvariantCulture)
                    : DefaultValue;
            case OptionKind.Toggle:
                return bool.TryParse(value, out var flag) ? (flag ? "true" : "false") : DefaultValue;
            default:
                return value;
        }
    }
}

/// <summary>Validated option values for one conversion.</summary>
public sealed class ConversionOptions
{
    private readonly Dictionary<string, string> _values;

    public ConversionOptions(IReadOnlyDictionary<string, string>? values = null) =>
        _values = values is null ? new(StringComparer.OrdinalIgnoreCase) : new(values, StringComparer.OrdinalIgnoreCase);

    public static ConversionOptions Empty => new();

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Builds options for a capability, normalizing every declared option against its descriptor.</summary>
    public static ConversionOptions For(ConversionCapability capability, IReadOnlyDictionary<string, string>? requested)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in capability.Options)
        {
            string? value = null;
            requested?.TryGetValue(descriptor.Key, out value);
            values[descriptor.Key] = descriptor.Normalize(value);
        }

        return new ConversionOptions(values);
    }

    public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) ? value : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    public bool GetBool(string key, bool fallback) =>
        _values.TryGetValue(key, out var value) && bool.TryParse(value, out var flag) ? flag : fallback;
}
