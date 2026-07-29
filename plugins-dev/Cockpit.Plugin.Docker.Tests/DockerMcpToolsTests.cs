using System.Text.Json.Nodes;
using Cockpit.Plugin.Docker.Engine;
using Cockpit.Plugin.Docker.Mcp;
using Cockpit.Plugin.Docker.Security;
using Cockpit.Plugin.Docker.Settings;
using Cockpit.Plugin.Docker.StatusBar;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Plugin.Docker.Tests;

public sealed class DockerMcpToolsTests
{
    private const string Session = "pane-1";

    private sealed record Harness(
        DockerMcpTools Tools,
        List<ConsentRequest> Asked,
        FakeDockerEngine Engine,
        FakeComposeCli Compose,
        FakeDockerCli Docker,
        RunningContainerRegistry Running);

    private static Harness _Build(ConsentOutcome outcome, bool allowExec = false)
    {
        var settings = new DockerSettings(new FakePluginStorage()) { AllowExec = allowExec };
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        var gate = new DockerAccessGate(host);
        var engine = new FakeDockerEngine();
        var compose = new FakeComposeCli();
        var docker = new FakeDockerCli();
        var running = new RunningContainerRegistry(engine, () => DateTimeOffset.UnixEpoch);
        return new Harness(new DockerMcpTools(settings, gate, engine, compose, docker, running), asked, engine, compose, docker, running);
    }

    // ---- Reads -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListContainers_WhenApproved_ReturnsTheContainers_AndAsksConnectionConsentOnce()
    {
        var h = _Build(ConsentOutcome.Approved);
        h.Engine.Containers = new[]
        {
            new DockerContainer("abc123", "web", "nginx:latest", "running", "Up 2 minutes",
                new[] { new DockerPortMapping("tcp", 80, 8080, "0.0.0.0") }),
        };

        var json = JsonNode.Parse(await h.Tools.ListContainers(Session));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(1, json["count"]!.GetValue<int>());
        Assert.Equal("web", json["containers"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(8080, json["containers"]![0]!["ports"]![0]!["publicPort"]!.GetValue<int>());
        Assert.Single(h.Asked);
        Assert.Equal(ConsentRisk.LowRisk, h.Asked[0].Risk);
        Assert.True(h.Asked[0].AllowRemember);
    }

    [Fact]
    public async Task ListContainers_WhenDeclined_ReturnsError_AndDoesNotTouchTheEngine()
    {
        var h = _Build(ConsentOutcome.Denied);
        h.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var json = JsonNode.Parse(await h.Tools.ListContainers(Session));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task DaemonInfo_ReturnsVersion_AndReflectsTheExecSetting()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: true);
        h.Engine.Info = new DockerDaemonInfo("27.1.0", "1.48", "linux", "arm64");

        var json = JsonNode.Parse(await h.Tools.DaemonInfo(Session));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("27.1.0", json["serverVersion"]!.GetValue<string>());
        Assert.True(json["execEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DaemonInfo_WhenTheDaemonIsUnreachable_ReturnsASanitizedError()
    {
        var h = _Build(ConsentOutcome.Approved);
        h.Engine.Throw = new TimeoutException("connect ECONNREFUSED /var/run/docker.sock");

        var json = JsonNode.Parse(await h.Tools.DaemonInfo(Session));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("could not be reached", json["error"]!.GetValue<string>());
        Assert.DoesNotContain("docker.sock", json["error"]!.GetValue<string>());
    }

    // ---- Mutations ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task StopContainer_WhenApproved_CallsTheEngine_AndAsksAfreshAsDangerous()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.StopContainer(Session, "web"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("web", Assert.Single(h.Engine.Stopped));
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
        Assert.False(h.Asked.Last().AllowRemember);
    }

    [Fact]
    public async Task RemoveContainer_WhenDeclined_DoesNotTouchTheEngine()
    {
        var h = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await h.Tools.RemoveContainer(Session, "web", force: true));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(h.Engine.Removed);
    }

    // ---- exec / run --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exec_WhenCapabilityOff_IsBlocked_WithoutTouchingTheEngineOrPrompting()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: false);

        var json = JsonNode.Parse(await h.Tools.Exec(Session, "web", "ls -la"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("settings", json["error"]!.GetValue<string>());
        Assert.Empty(h.Engine.Execs);
        Assert.Empty(h.Asked);
    }

    [Fact]
    public async Task Exec_WhenCapabilityOn_RunsAsShellCommand_AndReturnsOutput()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: true);
        h.Engine.ExecResultValue = new ExecResult(0, "hello", string.Empty);

        var json = JsonNode.Parse(await h.Tools.Exec(Session, "web", "echo hello"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("hello", json["stdout"]!.GetValue<string>());
        Assert.Single(h.Engine.Execs);
        Assert.Equal(new[] { "/bin/sh", "-c", "echo hello" }, h.Engine.Execs[0].Command);
    }

    [Fact]
    public async Task RunContainer_ShowsTheVerbatimCommand_WithDangerousFlags_AndTracksTheContainer()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: true);
        h.Engine.RunReturnsId = "deadbeef";

        var json = JsonNode.Parse(await h.Tools.RunContainer(
            Session, "nginx:latest", name: "web", publish: ["8080:80"], volumes: ["/:/host"], privileged: true));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("deadbeef", json["id"]!.GetValue<string>());

        var dangerRequest = h.Asked.Last();
        Assert.Equal(ConsentRisk.Dangerous, dangerRequest.Risk);
        Assert.Contains("--privileged", dangerRequest.Action);
        Assert.Contains("-v /:/host", dangerRequest.Action);
        Assert.Contains("nginx:latest", dangerRequest.Action);

        Assert.Single(h.Engine.Runs);
        Assert.Equal("web", Assert.Single(h.Running.Snapshot()).Title);
    }

    // ---- Compose -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ComposeUp_WhenApproved_RunsTheCli_AsADangerousChange()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.ComposeUp(Session, "/srv/app", file: "docker-compose.yml", services: ["web"]));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(h.Compose.Calls);
        Assert.Equal("/srv/app", h.Compose.Calls[0].Directory);
        Assert.Equal(new[] { "-f", "docker-compose.yml", "up", "-d", "--", "web" }, h.Compose.Calls[0].Args);
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
        Assert.False(h.Asked.Last().AllowRemember);
    }

    [Fact]
    public async Task ComposeConfig_IsARead_NeedingOnlyConnectionConsent()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.ComposeConfig(Session, "/srv/app"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(new[] { "config" }, h.Compose.Calls[0].Args);
        Assert.Single(h.Asked);
        Assert.Equal(ConsentRisk.LowRisk, h.Asked[0].Risk);
    }

    // ---- AC-93: logs, images, pull ----------------------------------------------------------------------------

    [Fact]
    public async Task Logs_WhenApproved_ReturnsOutput_AndIsARead()
    {
        var h = _Build(ConsentOutcome.Approved);
        h.Engine.LogsValue = new ContainerLogs("hello from stdout", "a warning");

        var json = JsonNode.Parse(await h.Tools.Logs(Session, "web", tail: 50));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("hello from stdout", json["stdout"]!.GetValue<string>());
        Assert.Equal("a warning", json["stderr"]!.GetValue<string>());
        Assert.Equal(("web", 50), Assert.Single(h.Engine.LogReads));
        Assert.Single(h.Asked);
        Assert.Equal(ConsentRisk.LowRisk, h.Asked[0].Risk);
    }

    [Fact]
    public async Task ListImages_WhenApproved_ReturnsImages()
    {
        var h = _Build(ConsentOutcome.Approved);
        h.Engine.Images = new[] { new DockerImage("abc", new[] { "nginx:latest" }, 142_000_000) };

        var json = JsonNode.Parse(await h.Tools.ListImages(Session));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(1, json["count"]!.GetValue<int>());
        Assert.Equal("nginx:latest", json["images"]![0]!["tags"]![0]!.GetValue<string>());
        Assert.Equal(ConsentRisk.LowRisk, h.Asked[0].Risk);
    }

    [Fact]
    public async Task PullImage_WhenApproved_PullsAndAsksMutationConsentEachTime()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.PullImage(Session, "nginx:1.27"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("nginx:1.27", Assert.Single(h.Engine.Pulled));
        // A mutation first clears the one-time connection consent, then asks for the change itself.
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
        Assert.False(h.Asked.Last().AllowRemember);
        Assert.Contains("nginx:1.27", h.Asked.Last().Action);
    }

    [Fact]
    public async Task PullImage_WhenDeclined_DoesNotTouchTheEngine()
    {
        var h = _Build(ConsentOutcome.Denied);
        h.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var json = JsonNode.Parse(await h.Tools.PullImage(Session, "nginx:1.27"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(h.Engine.Pulled);
    }

    [Fact]
    public async Task RunContainer_WhenImageMissing_ReturnsAClearPullHint_NotADaemonError()
    {
        // AC-93: a missing local image used to surface "daemon could not be reached (DockerImageNotFoundException)",
        // sending operators to look at the endpoint. It now names the image and points at pull_image.
        var h = _Build(ConsentOutcome.Approved, allowExec: true);
        h.Engine.Throw = new ImageNotFoundException("nginx:latest");

        var json = JsonNode.Parse(await h.Tools.RunContainer(Session, "nginx:latest"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        var error = json["error"]!.GetValue<string>();
        Assert.Contains("nginx:latest", error);
        Assert.Contains("pull_image", error);
        Assert.DoesNotContain("daemon could not be reached", error);
    }

    [Fact]
    public async Task ComposeLogs_IsARead_PassingTailAndServices()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.ComposeLogs(Session, "/srv/app", services: new[] { "web" }, tail: 100));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(new[] { "logs", "--no-color", "--no-log-prefix", "--tail", "100", "--", "web" }, h.Compose.Calls[0].Args);
        Assert.Equal(ConsentRisk.LowRisk, h.Asked[0].Risk);
    }

    // ---- AC-93 remaining tiers: consent levels ----------------------------------------------------------------

    [Fact]
    public async Task Inspect_And_Stats_And_Top_And_Volumes_And_Networks_And_ComposePs_AreReads()
    {
        // Every read needs only the one-time connection consent (LowRisk, remembered).
        foreach (var read in new Func<Harness, Task<string>>[]
        {
            h => h.Tools.Inspect(Session, "web"),
            h => h.Tools.Stats(Session, "web"),
            h => h.Tools.Top(Session, "web"),
            h => h.Tools.ListVolumes(Session),
            h => h.Tools.ListNetworks(Session),
            h => h.Tools.ComposePs(Session, "/srv/app"),
        })
        {
            var h = _Build(ConsentOutcome.Approved);
            var json = JsonNode.Parse(await read(h));
            Assert.True(json!["ok"]!.GetValue<bool>());
            Assert.Single(h.Asked);
            Assert.Equal(ConsentRisk.LowRisk, h.Asked[0].Risk);
            Assert.True(h.Asked[0].AllowRemember);
        }
    }

    [Fact]
    public async Task Tag_IsAMutation_Dangerous_NeverRemembered_ShowingBothReferences()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.Tag(Session, "myapp:latest", "reg/myapp:1.2"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(("myapp:latest", "reg/myapp:1.2"), Assert.Single(h.Engine.Tagged));
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
        Assert.False(h.Asked.Last().AllowRemember);
        Assert.Contains("reg/myapp:1.2", h.Asked.Last().Action);
    }

    [Fact]
    public async Task RemoveVolume_IsDestructive_Dangerous_ShowingTheVolume()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.RemoveVolume(Session, "pgdata", force: true));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(("pgdata", true), Assert.Single(h.Engine.RemovedVolumes));
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
        Assert.Contains("pgdata", h.Asked.Last().Action);
    }

    [Fact]
    public async Task Prune_AsksDangerousShowingTheTarget_AndReportsReclaimed()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.Prune(Session, "volumes"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(4096, json["spaceReclaimedBytes"]!.GetValue<long>());
        Assert.Equal(PruneTarget.Volumes, Assert.Single(h.Engine.Pruned));
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
        Assert.Contains("volumes", h.Asked.Last().Action);
    }

    [Fact]
    public async Task Prune_InvalidTarget_ErrorsWithoutAsking()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.Prune(Session, "everything"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(h.Asked);
        Assert.Empty(h.Engine.Pruned);
    }

    [Fact]
    public async Task Push_IsDangerous_NeverRemembered_AndRunsTheDockerCli()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.Push(Session, "reg/myapp:1.2"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(new[] { "push", "reg/myapp:1.2" }, Assert.Single(h.Docker.Calls));
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
        Assert.False(h.Asked.Last().AllowRemember);
    }

    [Fact]
    public async Task BuildImage_And_Cp_AreBlockedWhenExecIsOff()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: false);

        Assert.False(JsonNode.Parse(await h.Tools.BuildImage(Session, "/ctx", "myapp:latest"))!["ok"]!.GetValue<bool>());
        Assert.False(JsonNode.Parse(await h.Tools.Cp(Session, "web:/app/log", "/tmp/log"))!["ok"]!.GetValue<bool>());
        Assert.Empty(h.Docker.Calls);
    }

    [Fact]
    public async Task BuildImage_And_Cp_RunTheDockerCli_WhenExecOnAndApproved()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: true);

        Assert.True(JsonNode.Parse(await h.Tools.BuildImage(Session, "/ctx", "myapp:latest", dockerfile: "Dockerfile.prod"))!["ok"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(await h.Tools.Cp(Session, "web:/app/log", "/tmp/log"))!["ok"]!.GetValue<bool>());

        Assert.Equal(new[] { "build", "-t", "myapp:latest", "-f", "Dockerfile.prod", "/ctx" }, h.Docker.Calls[0]);
        Assert.Equal(new[] { "cp", "web:/app/log", "/tmp/log" }, h.Docker.Calls[1]);
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
    }
}
