using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// AC-510[b] criterion 5: "is this plugin an AI provider" is carried by the existing <c>category</c> field —
/// measured against the live default store on 2026-08-02 (exactly the five provider ids carry
/// <see cref="PluginStoreEntry.ProviderCategory"/>, nothing else does), locked in here through the real
/// deserializer over a fixture shaped like that index, not a hand-built list of records.
/// </summary>
public class PluginStoreEntryProviderCategoryTests
{
    [Fact]
    public void ProviderCategory_IsTheExactStringTheLiveIndexUses() =>
        Assert.Equal("AI providers", PluginStoreEntry.ProviderCategory);

    [Fact]
    public void RealIndex_FilteringByProviderCategory_KeepsOnlyTheProviderPlugins()
    {
        const string json = """
        {
          "name": "AI-Cockpit Plugins",
          "plugins": [
            { "id": "git-status", "name": "Git status", "latestVersion": "1.0.0", "category": "Productivity", "versions": [] },
            { "id": "claude-provider", "name": "Claude Code", "latestVersion": "0.14.1", "category": "AI providers", "versions": [] },
            { "id": "youtrack", "name": "YouTrack", "latestVersion": "1.0.0", "category": "Issue trackers", "versions": [] },
            { "id": "cli-agent-provider", "name": "Codex (ChatGPT)", "latestVersion": "0.5.3", "category": "AI providers", "versions": [] },
            { "id": "docker", "name": "Docker", "latestVersion": "1.0.0", "category": "Automation", "versions": [] }
          ]
        }
        """;

        Assert.True(PluginStoreIndex.TryParse(json, out var index, out _));

        var providers = index!.Plugins.Where(entry => entry.Category == PluginStoreEntry.ProviderCategory).ToList();

        Assert.Equal(["claude-provider", "cli-agent-provider"], providers.Select(entry => entry.Id));
    }

    [Fact]
    public void RealIndex_EntryWithNoCategory_IsNeverMistakenForAProvider()
    {
        const string json = """
        {
          "name": "Some Store",
          "plugins": [
            { "id": "mystery", "name": "Mystery plugin", "latestVersion": "1.0.0", "versions": [] }
          ]
        }
        """;

        Assert.True(PluginStoreIndex.TryParse(json, out var index, out _));

        Assert.DoesNotContain(index!.Plugins, entry => entry.Category == PluginStoreEntry.ProviderCategory);
    }
}
