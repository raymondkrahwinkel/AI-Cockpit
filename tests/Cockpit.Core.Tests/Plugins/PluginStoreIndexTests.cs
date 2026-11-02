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
    [InlineData("""
        {
          "name": "S",
          "plugins": [
            { "id": "a", "name": "A", "latestVersion": "1.2.0", "category": "Issue trackers", "icon": "🐛", "homepage": "https://example.com/a", "repository": "https://github.com/example/plugins", "featured": true, "published": "2026-05-12", "versions": [] },
            { "id": "a", "name": "A", "description": "d", "author": "me", "latestVersion": "1.2.0", "versions": [] },
            { "id": "a", "name": "A", "latestVersion": "1.2.0", "audience": ["developer"], "versions": [] },
            { "id": "a", "name": "A", "latestVersion": "1.0.0", "hidden": true, "versions": [] },
            { "id": "a", "name": "A", "latestVersion": "1.0.0", "icon": "🐛", "versions": [] },
            { "id": "depot", "name": "Depot", "latestVersion": "1.0.0", "icon": "🗄️", "logoAsset": "depot.svg", "versions": [] }
          ]
        }
        """)]
    public void TryParse_ReadsTheOptionalEntryFields_AndDefaultsWhatTheIndexOmits(string json)
    {
        Assert.True(PluginStoreIndex.TryParse(json, out var index, out var error), error);

        Assert.NotNull(index);
        Assert.Equal(
            new (string? Category, string? Icon, string? Homepage, string? Repository, bool Featured, string? Published, string Audience, bool Hidden, string? LogoAsset)[]
            {
                ("Issue trackers", "🐛", "https://example.com/a", "https://github.com/example/plugins", true, "2026-05-12", "", false, null),
                (null, null, null, null, false, null, "", false, null),
                (null, null, null, null, false, null, "developer", false, null),
                (null, null, null, null, false, null, "", true, null),
                (null, "🐛", null, null, false, null, "", false, null),
                (null, "🗄️", null, null, false, null, "", false, "depot.svg"),
            },
            index.Plugins.Select(entry => (entry.Category, entry.Icon, entry.Homepage, entry.Repository, entry.Featured,
                entry.Published, string.Join(',', entry.Audience ?? []), entry.Hidden, entry.LogoAsset)));
    }
}
