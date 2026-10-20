using System.Text.Json;

namespace FileOrbit.Core.Updates;

/// <summary>A published FileOrbit release.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Title, Uri PageUrl, DateTimeOffset? PublishedAt);

/// <summary>
/// Reads FileOrbit's releases from GitHub. Only the version number and release page are used; the
/// app never downloads or runs anything by itself. Links are accepted only if they point at this
/// repository's own release pages.
/// </summary>
public static class ReleaseFeed
{
    public const string Repository = "habdelkaderr/FileOrbit";

    public static Uri LatestReleaseApi { get; } = new($"https://api.github.com/repos/{Repository}/releases/latest");

    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases/latest");

    /// <summary>Parses GitHub's "latest release" JSON. Returns null for drafts, pre-releases or unexpected data.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                Flag(root, "draft") || Flag(root, "prerelease") ||
                !root.TryGetProperty("tag_name", out var tagElement) || tagElement.GetString() is not { } tag ||
                TryParseVersion(tag) is not { } version)
            {
                return null;
            }

            var page = root.TryGetProperty("html_url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsed) && IsTrustedPage(parsed)
                ? parsed
                : ReleasesPage;
            var title = root.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString()! : $"FileOrbit {tag}";
            DateTimeOffset? published = root.TryGetProperty("published_at", out var date) && date.TryGetDateTimeOffset(out var when) ? when : null;
            return new ReleaseInfo(version, tag, title, page, published);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Accepts "v1.2.3", "1.2", "1.2.3.4"; ignores anything after a '-' (e.g. "-beta").</summary>
    public static Version? TryParseVersion(string text)
    {
        var trimmed = text.Trim().TrimStart('v', 'V');
        var dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            trimmed = trimmed[..dash];
        }

        return Version.TryParse(trimmed, out var version) ? Normalize(version) : null;
    }

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>True for https://github.com/{Repository}/releases/... only.</summary>
    public static bool IsTrustedPage(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{Repository}/releases", StringComparison.OrdinalIgnoreCase);

    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
