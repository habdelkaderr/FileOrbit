using FileOrbit.Core.Updates;

namespace FileOrbit.Tests.Unit;

public sealed class ReleaseFeedTests
{
    private const string Latest = """
        {"tag_name":"v0.2.0","name":"FileOrbit v0.2.0","draft":false,"prerelease":false,
         "html_url":"https://github.com/habdelkaderr/FileOrbit/releases/tag/v0.2.0","published_at":"2026-10-21T08:00:00Z"}
        """;

    [Fact]
    public void Parses_latest_release()
    {
        var release = ReleaseFeed.Parse(Latest);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("https://github.com/habdelkaderr/FileOrbit/releases/tag/v0.2.0", release.PageUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.3.0","draft":true}""")]
    [InlineData("""{"tag_name":"v0.3.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"latest-build"}""")]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    public void Ignores_drafts_prereleases_and_bad_data(string json) => Assert.Null(ReleaseFeed.Parse(json));

    [Theory]
    [InlineData("https://evil.example/FileOrbit/releases/tag/v9")]
    [InlineData("http://github.com/habdelkaderr/FileOrbit/releases/tag/v9")]
    [InlineData("https://github.com/someone-else/FileOrbit/releases/tag/v9")]
    [InlineData("https://github.com/habdelkaderr/FileOrbit/archive/v9.zip")]
    public void Untrusted_links_are_replaced_by_the_releases_page(string url)
    {
        var release = ReleaseFeed.Parse($$"""{"tag_name":"v9.0.0","html_url":"{{url}}"}""");

        Assert.Equal(ReleaseFeed.ReleasesPage, release!.PageUrl);
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("v0.1", "0.1.0", false)]
    [InlineData("v0.1.1-beta", "0.1.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.9.0", "1.0.0", false)]
    public void Version_comparison(string candidate, string current, bool newer) =>
        Assert.Equal(newer, ReleaseFeed.IsNewer(ReleaseFeed.TryParseVersion(candidate)!, Version.Parse(current)));
}
