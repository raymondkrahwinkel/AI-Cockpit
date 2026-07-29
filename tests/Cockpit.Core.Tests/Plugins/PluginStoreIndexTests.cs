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
    public void TryParse_MissingPluginsArray_YieldsEmpty()
    {
        Assert.True(PluginStoreIndex.TryParse("""{ "name": "Empty" }""", out var index, out _));
        Assert.Empty(index!.Plugins);
    }

    [Fact]
    public void TryParse_InvalidJson_Fails()
    {
        Assert.False(PluginStoreIndex.TryParse("{ not json", out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryParse_EntryWithStoreDialogFields_ReadsAllSix()
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
              "category": "Issue trackers",
              "icon": "🐛",
              "homepage": "https://example.com/github-issues",
              "repository": "https://github.com/example/plugins",
              "featured": true,
              "published": "2026-05-12",
              "versions": [
                { "version": "1.2.0", "path": "github-issues/gh-1.2.0.zip", "abstractionsVersion": 1, "minHostVersion": "1.0.0", "sha256": "abc", "notes": "n" }
              ]
            }
          ]
        }
        """;

        Assert.True(PluginStoreIndex.TryParse(json, out var index, out _));
        var entry = index!.Plugins[0];
        Assert.Equal("Issue trackers", entry.Category);
        Assert.Equal("🐛", entry.Icon);
        Assert.Equal("https://example.com/github-issues", entry.Homepage);
        Assert.Equal("https://github.com/example/plugins", entry.Repository);
        Assert.True(entry.Featured);
        Assert.Equal("2026-05-12", entry.Published);
    }

    [Fact]
    public void TryParse_EntryWithoutStoreDialogFields_YieldsNeatDefaults()
    {
        // Mirrors today's production index.json — none of the #62 fields exist yet.
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
                { "version": "1.2.0", "path": "github-issues/gh-1.2.0.zip", "abstractionsVersion": 1, "minHostVersion": "1.0.0", "sha256": "abc", "notes": "n" }
              ]
            }
          ]
        }
        """;

        Assert.True(PluginStoreIndex.TryParse(json, out var index, out _));
        var entry = index!.Plugins[0];
        Assert.Null(entry.Category);
        Assert.Null(entry.Icon);
        Assert.Null(entry.Homepage);
        Assert.Null(entry.Repository);
        Assert.False(entry.Featured);
        Assert.Null(entry.Published);
    }
}
