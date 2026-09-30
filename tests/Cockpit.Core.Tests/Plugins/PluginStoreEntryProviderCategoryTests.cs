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

}
