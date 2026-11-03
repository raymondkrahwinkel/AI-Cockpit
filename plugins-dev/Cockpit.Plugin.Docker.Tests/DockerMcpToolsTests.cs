using System.Text.Json.Nodes;
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
        var gate = new DockerAccessGate(host, settings);
        var engine = new FakeDockerEngine();
        var compose = new FakeComposeCli();
        var docker = new FakeDockerCli();
        var running = new RunningContainerRegistry(engine, () => DateTimeOffset.UnixEpoch);
        return new Harness(new DockerMcpTools(settings, gate, engine, compose, docker, running), asked, engine, compose, docker, running);
    }

    // ---- Reads -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListContainers_WhenDeclined_ReturnsError_AndDoesNotTouchTheEngine()
    {
        var h = _Build(ConsentOutcome.Denied);
        h.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var json = JsonNode.Parse(await h.Tools.ListContainers(Session));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    // ---- Mutations ---------------------------------------------------------------------------------------------

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

    // ---- AC-93: logs, images, pull ----------------------------------------------------------------------------

    // ---- AC-93 remaining tiers: consent levels ----------------------------------------------------------------

    [Fact]
    public async Task BuildImage_And_Cp_AreBlockedWhenExecIsOff()
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: false);

        Assert.False(JsonNode.Parse(await h.Tools.BuildImage(Session, "/ctx", "myapp:latest"))!["ok"]!.GetValue<bool>());
        Assert.False(JsonNode.Parse(await h.Tools.Cp(Session, "web:/app/log", "/tmp/log"))!["ok"]!.GetValue<bool>());
        Assert.Empty(h.Docker.Calls);
    }
}
