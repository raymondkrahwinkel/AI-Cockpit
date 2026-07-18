using Cockpit.Plugin.Docker.StatusBar;
using FluentAssertions;

namespace Cockpit.Plugin.Docker.Tests;

public sealed class RunningContainerRegistryTests
{
    [Fact]
    public void Track_ThenSnapshot_ShowsTheContainer_WithImagePortsSessionAndUptime()
    {
        var start = DateTimeOffset.UnixEpoch;
        var now = start;
        var engine = new FakeDockerEngine();
        var registry = new RunningContainerRegistry(engine, () => now);

        registry.Track("abc123def456ghi", "web", "nginx:latest", "8080:80", "pane-1");
        now = start + TimeSpan.FromMinutes(3);

        var snapshot = registry.Snapshot();

        snapshot.Should().ContainSingle();
        var activity = snapshot[0];
        activity.Title.Should().Be("web");
        activity.Details.Should().Contain(detail => detail.Label == "Image" && detail.Value == "nginx:latest");
        activity.Details.Should().Contain(detail => detail.Label == "Ports" && detail.Value == "8080:80");
        activity.Details.Should().Contain(detail => detail.Label == "Session" && detail.Value == "pane-1");
        activity.Details.Should().Contain(detail => detail.Label == "Uptime" && detail.Value == "3m00s");
    }

    [Fact]
    public void Snapshot_UsesShortId_WhenTheContainerHasNoName()
    {
        var engine = new FakeDockerEngine();
        var registry = new RunningContainerRegistry(engine, () => DateTimeOffset.UnixEpoch);

        registry.Track("abc123def456ghi789", name: "", "nginx", string.Empty, "pane-1");

        registry.Snapshot()[0].Title.Should().Be("abc123def456");
    }

    [Fact]
    public async Task StopAsync_RemovesTheContainer_ForcefullyThroughTheEngine_AndRaisesChanged()
    {
        var engine = new FakeDockerEngine();
        var registry = new RunningContainerRegistry(engine, () => DateTimeOffset.UnixEpoch);
        registry.Track("abc123", "web", "nginx", "8080:80", "pane-1");

        var changed = 0;
        registry.Changed += () => changed++;

        await registry.Snapshot()[0].StopAsync();

        engine.Removed.Should().ContainSingle().Which.Should().Be(("abc123", true));
        registry.Snapshot().Should().BeEmpty();
        changed.Should().Be(1);
    }
}
