using Cockpit.App.Plugins;

namespace Cockpit.App.ViewTests;

public sealed class PluginStorageRemoveTests
{
    [Fact]
    public void Remove_DropsRestoredCacheKey_AndDoesNotPersistAgainWhenAlreadyGone()
    {
        var writes = 0;
        IReadOnlyDictionary<string, string>? saved = null;
        var storage = new PluginStorage(new Dictionary<string, string> { ["runs"] = "[]" }, data =>
        {
            writes++;
            saved = data;
        });

        storage.Remove("runs");
        storage.Remove("runs");

        Assert.Equal(1, writes);
        Assert.NotNull(saved);
        Assert.DoesNotContain("runs", saved!);
    }
}
