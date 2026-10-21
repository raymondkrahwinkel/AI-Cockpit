using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.TestSupport;

namespace Cockpit.Journeys;

[Collection(JourneyCollection.Alone)]
public sealed class WorktreeCleanupJourney
{
    [Fact]
    public async Task StoppedSessions_AreSweptAfterPush_WhileUnpushedWorkAndLiveResourcesStay()
    {
        using var fixture = new CleanupFixture();
        await fixture.InitializeAsync();
        await using var cockpit = JourneyHost.Api(services =>
        {
            services.AddSingleton<IWorktreeManager>(fixture.Manager);
            services.AddSingleton<IWorktreeRegistry>(fixture.Registry);
            services.AddSingleton<IDockerCli>(fixture.Docker);
        });
        cockpit.Backend.Start();
        var project = await cockpit.SaveProfileAndProjectAsync(fixture.Repository);
        var launcher = cockpit.Services.GetRequiredService<ISessionLauncher>();
        var desk = await launcher.CreateSessionsWorkspaceAsync("Cleanup");
        var profile = new SessionProfile(JourneyHost.EchoProfile, new ClaudeConfig(Path.Combine(cockpit.StateRoot, ".claude")))
        {
            DefaultKind = ProfileSessionKind.Sdk,
        };
        async Task<string> StartAsync(string name)
        {
            var started = await launcher.StartSessionAsync(new(desk.Id, profile, null, fixture.Repository, name, null, null, false, project.Id, false));
            return started?.PaneId ?? throw new InvalidOperationException("The session did not start.");
        }

        var finished = await StartAsync("finished");
        var unpushed = await StartAsync("unpushed");
        var alive = await StartAsync("alive");
        var live = cockpit.Services.GetRequiredService<ILiveSessionRegistry>();
        var endpoint = cockpit.Services.GetServices<ICockpitInternalMcpProvider>().SelectMany(provider => provider.GetServers())
            .Single(server => server.Name == "cockpit-worktrees").Url ?? throw new InvalidOperationException("No worktree endpoint.");
        async Task<string> GateAsync(string pane, string branch)
        {
            await using var client = await cockpit.ConnectAsPaneAsync(pane, "cockpit-worktrees", endpoint);
            var result = await JourneyHost.CallAsync(client, "worktree_create", new()
            {
                ["session"] = pane,
                ["directory"] = fixture.Repository,
                ["branch"] = branch,
            });
            Assert.True(result["ok"]?.GetValue<bool>());
            return result["path"]?.GetValue<string>() ?? throw new InvalidOperationException("No worktree path.");
        }

        var gate = await GateAsync(finished, "gate");
        var keep = await GateAsync(unpushed, "keep");
        foreach (var path in new[] { gate, keep })
        {
            await File.WriteAllTextAsync(Path.Combine(path, "work.txt"), Path.GetFileName(path));
            await CleanupFixture.GitAsync(path, "add", ".");
            await CleanupFixture.GitAsync(path, "commit", "-m", "session work");
        }

        var temp = Directory.CreateDirectory(Path.Combine(fixture.TempRoot, finished)).FullName;
        await File.WriteAllTextAsync(Path.Combine(temp, "test.txt"), "temporary");
        var liveTemp = Directory.CreateDirectory(Path.Combine(fixture.TempRoot, alive)).FullName;
        await File.WriteAllTextAsync(Path.Combine(liveTemp, "live.txt"), "still running");
        fixture.Docker.Add("orphan", "postgres:17", new() { ["org.testcontainers"] = "true", ["org.testcontainers.session-id"] = "gone" });
        fixture.Docker.Add("live", "app", new() { ["com.docker.compose.project"] = "live", ["com.docker.compose.project.working_dir"] = liveTemp });

        await launcher.StopSessionAsync(finished);
        await launcher.StopSessionAsync(unpushed);
        Assert.All(await fixture.Registry.ListAsync(), record => Assert.True(record.IsRetained));
        await CleanupFixture.GitAsync(gate, "push", "origin", "HEAD:gate");
        await CleanupFixture.GitAsync(fixture.Repository, "update-ref", "-d", "refs/remotes/origin/gate");
        Assert.True(Directory.Exists(gate));

        using var reconciler = new WorktreeReconciler(fixture.Manager) { LiveSessionIds = () => live.LiveSessionIds.ToArray() };
        await reconciler.RunOnceAsync();

        Assert.False(Directory.Exists(gate));
        Assert.False(Directory.Exists(temp));
        Assert.Contains("orphan", fixture.Docker.Removed);
        Assert.True(Directory.Exists(keep));
        Assert.True(File.Exists(Path.Combine(liveTemp, "live.txt")));
        Assert.DoesNotContain("live", fixture.Docker.Removed);
        var retained = Assert.Single(await fixture.Registry.ListAsync());
        Assert.Equal("1 commit not on the remote", retained.RetentionReason);
        await using var observer = await cockpit.ConnectAsPaneAsync(alive, "cockpit-worktrees", endpoint);
        var listing = await JourneyHost.CallAsync(observer, "worktree_list", new());
        Assert.Equal(retained.RetentionReason, listing["worktrees"]?[0]?["retentionReason"]?.GetValue<string>());
        Assert.Equal("session work", await CleanupFixture.GitAsync(keep, "log", "-1", "--format=%s"));
    }
}
