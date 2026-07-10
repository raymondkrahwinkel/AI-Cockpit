using Cockpit.Core.Plugins;
using FluentAssertions;

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

        PluginStoreIndex.TryParse(json, out var index, out _).Should().BeTrue();
        index!.Name.Should().Be("My Store");
        index.Plugins.Should().ContainSingle();

        var entry = index.Plugins[0];
        entry.Id.Should().Be("github-issues");
        entry.LatestVersion.Should().Be("1.2.0");
        entry.Versions.Should().HaveCount(2);
        entry.Versions[0].Path.Should().Be("github-issues/gh-1.2.0.zip");
        entry.Versions[0].Sha256.Should().Be("abc");
    }

    [Fact]
    public void TryParse_MissingPluginsArray_YieldsEmpty()
    {
        PluginStoreIndex.TryParse("""{ "name": "Empty" }""", out var index, out _).Should().BeTrue();
        index!.Plugins.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_InvalidJson_Fails()
    {
        PluginStoreIndex.TryParse("{ not json", out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }
}
