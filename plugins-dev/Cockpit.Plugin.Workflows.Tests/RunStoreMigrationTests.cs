using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

public sealed class RunStoreMigrationTests
{
    [Fact]
    public async Task Add_KeepsTheLatestRunPerWorkflowBeyondTheGlobalTwenty()
    {
        var store = new RunStore(new InMemoryPluginStorage());
        store.Add(_Run("flow-a", 0));

        for (var index = 1; index <= 25; index++)
        {
            store.Add(_Run("flow-b", index));
        }

        Assert.Equal("run-0", Assert.Single(store.For("flow-a")).Id);
        Assert.Equal(20, store.For("flow-b").Count);

        Migrate_CopiesLegacyRunsBeforeRemovingThem_AndKeepsAnExistingCache();
        ScheduleTests.Verify();
        await WorkflowRunsHealthTests.VerifyAsync();
    }

    private static void Migrate_CopiesLegacyRunsBeforeRemovingThem_AndKeepsAnExistingCache()
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

    private static WorkflowRun _Run(string workflowId, int index) => new()
    {
        Id = $"run-{index}",
        WorkflowId = workflowId,
        WorkflowName = workflowId,
        StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(index),
        FinishedAt = DateTimeOffset.UnixEpoch.AddMinutes(index + 1),
        Status = RunStatus.Succeeded,
    };
}
