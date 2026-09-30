using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Parsing a store's index.json (#14): a valid catalogue, a missing plugins array, and invalid JSON.</summary>
public class PluginStoreIndexTests
{
    [Fact]
    public void TryParse_ValidCatalogue_ReadsEntriesAndVersions()
    {
        const string json = """
        {
          "name": "My Store",
          "plugins": [
            {
              "id": "github-issues",
              "name": "GitHub Issues",
              "description": "d",
              "author": "me",
              "latestVersion": "1.2.0",
              "versions": [
                { "version": "1.2.0", "path": "github-issues/gh-1.2.0.zip", "abstractionsVersion": 1, "minHostVersion": "1.0.0", "sha256": "abc", "notes": "n" },
                { "version": "1.1.0", "path": "github-issues/gh-1.1.0.zip", "abstractionsVersion": 1 }
              ]
            }
          ]
        }
        """;

        Assert.True(PluginStoreIndex.TryParse(json, out var index, out _));
        Assert.Equal("My Store", index!.Name);
        Assert.Single(index.Plugins);

        var entry = index.Plugins[0];
        Assert.Equal("github-issues", entry.Id);
        Assert.Equal("1.2.0", entry.LatestVersion);
        Assert.Equal(2, System.Linq.Enumerable.Count(entry.Versions));
        Assert.Equal("github-issues/gh-1.2.0.zip", entry.Versions[0].Path);
        Assert.Equal("abc", entry.Versions[0].Sha256);
    }

    [Fact]
    public void TryParse_InvalidJson_Fails()
    {
        Assert.False(PluginStoreIndex.TryParse("{ not json", out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // The optional entry fields, each as the index would carry them: the store-dialog fields (#62), the shipped index
    // without any of them, audience, hidden, and the additive `logoAsset` (AC-553, absent in an index published before it).
    [Theory]
    [InlineData("""{ "id": "a", "name": "A", "latestVersion": "1.2.0", "category": "Issue trackers", "icon": "🐛", "homepage": "https://example.com/a", "repository": "https://github.com/example/plugins", "featured": true, "published": "2026-05-12", "versions": [] }""",
        "Issue trackers|🐛|https://example.com/a|https://github.com/example/plugins|True|2026-05-12||False|")]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "author": "me", "latestVersion": "1.2.0", "versions": [] }""",
        "||||False|||False|")]
    [InlineData("""{ "id": "a", "name": "A", "latestVersion": "1.2.0", "audience": ["developer"], "versions": [] }""",
        "||||False||developer|False|")]
    [InlineData("""{ "id": "a", "name": "A", "latestVersion": "1.0.0", "hidden": true, "versions": [] }""",
        "||||False|||True|")]
    [InlineData("""{ "id": "a", "name": "A", "latestVersion": "1.0.0", "icon": "🐛", "versions": [] }""",
        "|🐛|||False|||False|")]
    [InlineData("""{ "id": "depot", "name": "Depot", "latestVersion": "1.0.0", "icon": "🗄️", "logoAsset": "depot.svg", "versions": [] }""",
        "|🗄️|||False|||False|depot.svg")]
    public void TryParse_ReadsTheOptionalEntryFields_AndDefaultsWhatTheIndexOmits(string entryJson, string expected)
    {
        Assert.True(PluginStoreIndex.TryParse($$"""{ "name": "S", "plugins": [ {{entryJson}} ] }""", out var index, out var error), error);

        Assert.NotNull(index);
        var entry = index.Plugins.Single();
        Assert.Equal(
            expected,
            $"{entry.Category}|{entry.Icon}|{entry.Homepage}|{entry.Repository}|{entry.Featured}|{entry.Published}|{string.Join(',', entry.Audience ?? [])}|{entry.Hidden}|{entry.LogoAsset}");
    }
}
