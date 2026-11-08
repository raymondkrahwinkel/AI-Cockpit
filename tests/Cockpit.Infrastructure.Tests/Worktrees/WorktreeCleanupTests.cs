using Cockpit.TestSupport;

namespace Cockpit.Infrastructure.Tests.Worktrees;

public sealed class WorktreeCleanupTests
{

    [Fact]
    public async Task Testcontainer_IsRemovedOnlyWithoutRyukAndWithoutALiveComposeProject()
    {
        {
            var scenario = new WorktreeCleanupTests();
            await scenario._AssertTestcontainer_IsRemovedOnlyWithoutRyukAndWithoutALiveComposeProjectAsync(true, false, false);
        }
        {
            var scenario = new WorktreeCleanupTests();
            await scenario._AssertTestcontainer_IsRemovedOnlyWithoutRyukAndWithoutALiveComposeProjectAsync(false, false, true);
        }
        {
            var scenario = new WorktreeCleanupTests();
            await scenario._AssertTestcontainer_IsRemovedOnlyWithoutRyukAndWithoutALiveComposeProjectAsync(false, true, false);
        }
    }

    private async Task _AssertTestcontainer_IsRemovedOnlyWithoutRyukAndWithoutALiveComposeProjectAsync(bool ryuk, bool liveCompose, bool removed)
    {
        using var fixture = new CleanupFixture();
        await fixture.InitializeAsync();
        var live = await fixture.Manager.CreateAsync("live", "live", fixture.Repository);
        fixture.Docker.Add("test", "postgres:17", new()
        {
            ["org.testcontainers"] = "true",
            ["org.testcontainers.session-id"] = "tests",
            ["com.docker.compose.project"] = "shared",
        });
        if (ryuk)
        {
            fixture.Docker.Add("ryuk", "testcontainers/ryuk:0.14.0", new() { ["org.testcontainers.session-id"] = "tests" });
        }

        if (liveCompose)
        {
            fixture.Docker.Add("live", "app", new()
            {
                ["com.docker.compose.project"] = "shared",
                ["com.docker.compose.project.working_dir"] = live.Path,
            });
        }

        await fixture.Manager.ReconcileAsync(["live"]);

        Assert.Equal(removed, fixture.Docker.Removed.Contains("test"));
        Assert.DoesNotContain("live", fixture.Docker.Removed);
        Assert.DoesNotContain("ryuk", fixture.Docker.Removed);
    }
}
