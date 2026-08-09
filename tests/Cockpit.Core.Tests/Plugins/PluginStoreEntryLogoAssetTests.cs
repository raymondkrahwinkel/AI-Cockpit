using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// AC-553: <c>logoAsset</c> is additive — an index published before this field existed (e.g. the one
/// <c>docs/plugins/example-store-index.json</c> ships as a real-world regression fixture) must keep parsing,
/// and an index that does set it must round-trip the value.
/// </summary>
public class PluginStoreEntryLogoAssetTests
{
    [Fact]
    public void EntryWithoutLogoAsset_StillParses_AndLogoAssetIsNull()
    {
        const string json = """
        {
          "name": "Some Store",
          "plugins": [
            { "id": "github-issues", "name": "GitHub Issues", "latestVersion": "1.0.0", "icon": "🐛", "versions": [] }
          ]
        }
        """;

        Assert.True(PluginStoreIndex.TryParse(json, out var index, out var error));
        Assert.Null(error);
        Assert.Null(index!.Plugins.Single().LogoAsset);
    }

    [Fact]
    public void EntryWithLogoAsset_ParsesTheFileName()
    {
        const string json = """
        {
          "name": "Some Store",
          "plugins": [
            { "id": "depot", "name": "Depot", "latestVersion": "1.0.0", "icon": "🗄️", "logoAsset": "depot.svg", "versions": [] }
          ]
        }
        """;

        Assert.True(PluginStoreIndex.TryParse(json, out var index, out _));
        Assert.Equal("depot.svg", index!.Plugins.Single().LogoAsset);
    }
}
