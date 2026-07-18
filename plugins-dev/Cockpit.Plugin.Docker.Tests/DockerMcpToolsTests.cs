using System.Text.Json.Nodes;
using Cockpit.Plugin.Docker.Engine;
using Cockpit.Plugin.Docker.Mcp;
using Cockpit.Plugin.Docker.Security;
using Cockpit.Plugin.Docker.Settings;
using Cockpit.Plugin.Docker.StatusBar;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using FluentAssertions;
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
        var running = new RunningContainerRegistry(engine, () => DateTimeOffset.UnixEpoch);
        return new Harness(new DockerMcpTools(settings, gate, engine, compose, running), asked, engine, compose, running);
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

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        json["count"]!.GetValue<int>().Should().Be(1);
        json["containers"]![0]!["name"]!.GetValue<string>().Should().Be("web");
        json["containers"]![0]!["ports"]![0]!["publicPort"]!.GetValue<int>().Should().Be(8080);
        h.Asked.Should().ContainSingle();
        h.Asked[0].Risk.Should().Be(ConsentRisk.LowRisk);
        h.Asked[0].AllowRemember.Should().BeTrue();
    }

    [Fact]
    public async Task ListContainers_WhenDeclined_ReturnsError_AndDoesNotTouchTheEngine()
    {
        var h = _Build(ConsentOutcome.Denied);
        h.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var json = JsonNode.Parse(await h.Tools.ListContainers(Session));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("did not approve");
    }

    [Fact]
    public async Task DaemonInfo_ReturnsVersion_AndReflectsTheExecSetting()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: true);
        h.Engine.Info = new DockerDaemonInfo("27.1.0", "1.48", "linux", "arm64");

        var json = JsonNode.Parse(await h.Tools.DaemonInfo(Session));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        json["serverVersion"]!.GetValue<string>().Should().Be("27.1.0");
        json["execEnabled"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task DaemonInfo_WhenTheDaemonIsUnreachable_ReturnsASanitizedError()
    {
        var h = _Build(ConsentOutcome.Approved);
        h.Engine.Throw = new TimeoutException("connect ECONNREFUSED /var/run/docker.sock");

        var json = JsonNode.Parse(await h.Tools.DaemonInfo(Session));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("could not be reached");
        json["error"]!.GetValue<string>().Should().NotContain("docker.sock", "the raw endpoint must not leak to the agent");
    }

    // ---- Mutations ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task StopContainer_WhenApproved_CallsTheEngine_AndAsksAfreshAsDangerous()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.StopContainer(Session, "web"));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        h.Engine.Stopped.Should().ContainSingle().Which.Should().Be("web");
        h.Asked.Last().Risk.Should().Be(ConsentRisk.Dangerous);
        h.Asked.Last().AllowRemember.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveContainer_WhenDeclined_DoesNotTouchTheEngine()
    {
        var h = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await h.Tools.RemoveContainer(Session, "web", force: true));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        h.Engine.Removed.Should().BeEmpty();
    }

    // ---- exec / run --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exec_WhenCapabilityOff_IsBlocked_WithoutTouchingTheEngineOrPrompting()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: false);

        var json = JsonNode.Parse(await h.Tools.Exec(Session, "web", "ls -la"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("settings");
        h.Engine.Execs.Should().BeEmpty();
        h.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task Exec_WhenCapabilityOn_RunsAsShellCommand_AndReturnsOutput()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: true);
        h.Engine.ExecResultValue = new ExecResult(0, "hello", string.Empty);

        var json = JsonNode.Parse(await h.Tools.Exec(Session, "web", "echo hello"));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        json["stdout"]!.GetValue<string>().Should().Be("hello");
        h.Engine.Execs.Should().ContainSingle();
        h.Engine.Execs[0].Command.Should().Equal("/bin/sh", "-c", "echo hello");
    }

    [Fact]
    public async Task RunContainer_ShowsTheVerbatimCommand_WithDangerousFlags_AndTracksTheContainer()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: true);
        h.Engine.RunReturnsId = "deadbeef";

        var json = JsonNode.Parse(await h.Tools.RunContainer(
            Session, "nginx:latest", name: "web", publish: ["8080:80"], volumes: ["/:/host"], privileged: true));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        json["id"]!.GetValue<string>().Should().Be("deadbeef");

        var dangerRequest = h.Asked.Last();
        dangerRequest.Risk.Should().Be(ConsentRisk.Dangerous);
        dangerRequest.Action.Should().Contain("--privileged");
        dangerRequest.Action.Should().Contain("-v /:/host");
        dangerRequest.Action.Should().Contain("nginx:latest");

        h.Engine.Runs.Should().ContainSingle();
        h.Running.Snapshot().Should().ContainSingle().Which.Title.Should().Be("web");
    }

    // ---- Compose -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ComposeUp_WhenApproved_RunsTheCli_AsADangerousChange()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.ComposeUp(Session, "/srv/app", file: "docker-compose.yml", services: ["web"]));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        h.Compose.Calls.Should().ContainSingle();
        h.Compose.Calls[0].Directory.Should().Be("/srv/app");
        h.Compose.Calls[0].Args.Should().Equal("-f", "docker-compose.yml", "up", "-d", "web");
        h.Asked.Last().Risk.Should().Be(ConsentRisk.Dangerous);
        h.Asked.Last().AllowRemember.Should().BeFalse();
    }

    [Fact]
    public async Task ComposeConfig_IsARead_NeedingOnlyConnectionConsent()
    {
        var h = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await h.Tools.ComposeConfig(Session, "/srv/app"));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        h.Compose.Calls[0].Args.Should().Equal("config");
        h.Asked.Should().ContainSingle();
        h.Asked[0].Risk.Should().Be(ConsentRisk.LowRisk);
    }
}
