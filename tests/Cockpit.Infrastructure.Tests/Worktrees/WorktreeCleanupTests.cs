using Cockpit.TestSupport;

namespace Cockpit.Infrastructure.Tests.Worktrees;

public sealed class WorktreeCleanupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingForeignLockedWorktree_IsUnlockedBeforePruning(bool registered)
    {
        using var fixture = new CleanupFixture();
        await fixture.InitializeAsync();
        var record = await fixture.Manager.CreateAsync("gone", "foreign", fixture.Repository);
        await fixture.Manager.ReleaseOwnershipAsync(record.Path);
        var admin = Directory.GetDirectories(Path.Combine(fixture.Repository, ".git", "worktrees")).Single();
        var foreign = $"/home/fedora/ac1537-{Guid.NewGuid():N}";
        Directory.Delete(record.Path, recursive: true);
        await File.WriteAllTextAsync(Path.Combine(admin, "gitdir"), foreign + "/.git\n");
        await fixture.Registry.RemoveAsync(record.Path);
        if (registered)
        {
            await fixture.Registry.AddAsync(record with { Path = foreign, IsRetained = true });
        }
        else
        {
            await fixture.Manager.CreateAsync("live", "live", fixture.Repository);
        }

        var missing = Assert.Single(await fixture.Manager.GetStatusesAsync(), status => status.Exists == false);
        Assert.Equal(foreign, missing.Record.Path);
        await fixture.Manager.RemoveAsync(missing.Record);

        Assert.False(Directory.Exists(admin));
        Assert.DoesNotContain(foreign, await CleanupFixture.GitAsync(fixture.Repository, "worktree", "list", "--porcelain"));
        Assert.DoesNotContain(await fixture.Registry.ListAsync(), row => row.Path == foreign);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public async Task Testcontainer_IsRemovedOnlyWithoutRyukAndWithoutALiveComposeProject(bool ryuk, bool liveCompose, bool removed)
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
