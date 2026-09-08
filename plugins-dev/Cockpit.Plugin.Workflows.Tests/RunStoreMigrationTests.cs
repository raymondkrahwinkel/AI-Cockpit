using Cockpit.Plugin.Workflows.Engine;

namespace Cockpit.Plugin.Workflows.Tests;

public sealed class RunStoreMigrationTests
{
    [Fact]
    public void Migrate_CopiesLegacyRunsBeforeRemovingThem_AndKeepsAnExistingCache()
    {
        var legacy = new InMemoryPluginStorage();
        var cache = new InMemoryPluginStorage();
        legacy.Set("runs", """[{"workflowId":"kept"}]""");

        RunStore.Migrate(legacy, cache);

        Assert.Equal("""[{"workflowId":"kept"}]""", cache.Get<string>("runs"));
        Assert.Null(legacy.Get<string>("runs"));

        legacy.Set("runs", "old");
        cache.Set("runs", "new");
        RunStore.Migrate(legacy, cache);

        Assert.Equal("new", cache.Get<string>("runs"));
        Assert.Null(legacy.Get<string>("runs"));
    }
}
