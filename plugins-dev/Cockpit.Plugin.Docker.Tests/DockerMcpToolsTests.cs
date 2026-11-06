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

    // A declined read and a declined change both answer with the refusal and never reach the engine.
    [Theory]
    [InlineData("list_containers")]
    [InlineData("remove_container")]
    public async Task WhenDeclined_ReturnsError_AndDoesNotTouchTheEngine(string tool)
    {
        var h = _Build(ConsentOutcome.Denied);
        h.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var json = JsonNode.Parse(tool == "list_containers"
            ? await h.Tools.ListContainers(Session)
            : await h.Tools.RemoveContainer(Session, "web", force: true));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
        Assert.Empty(h.Engine.Removed);
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

    // Every change asks afresh as Dangerous, is never remembered, and shows the operator what it touches — the
    // tools that go through the shared mutation path (stop, pull, tag, remove volume) and the ones with their own
    // gate call (prune, push).
    [Theory]
    [InlineData("stop_container", "web")]
    [InlineData("pull_image", "nginx:1.27")]
    [InlineData("tag", "reg/myapp:1.2")]
    [InlineData("remove_volume", "pgdata")]
    [InlineData("prune", "volumes")]
    [InlineData("push", "reg/myapp:1.2")]
    public async Task Mutation_WhenApproved_AsksAfreshAsDangerous_ShowingTheTarget(string tool, string shown)
    {
        var h = _Build(ConsentOutcome.Approved);

        var answer = tool switch
        {
            "stop_container" => await h.Tools.StopContainer(Session, "web"),
            "pull_image" => await h.Tools.PullImage(Session, "nginx:1.27"),
            "tag" => await h.Tools.Tag(Session, "myapp:latest", "reg/myapp:1.2"),
            "remove_volume" => await h.Tools.RemoveVolume(Session, "pgdata", force: true),
            "prune" => await h.Tools.Prune(Session, "volumes"),
            _ => await h.Tools.Push(Session, "reg/myapp:1.2"),
        };

        Assert.True(JsonNode.Parse(answer)!["ok"]!.GetValue<bool>());
        Assert.Equal(ConsentRisk.Dangerous, h.Asked.Last().Risk);
        Assert.False(h.Asked.Last().AllowRemember);
        Assert.Contains(shown, h.Asked.Last().Action);
    }

    // ---- exec / run --------------------------------------------------------------------------------------------

    // exec, build and cp run arbitrary code: with the capability off each is blocked with a settings hint, without
    // touching the engine or the docker CLI and without prompting.
    [Theory]
    [InlineData("exec")]
    [InlineData("build_image")]
    [InlineData("cp")]
    public async Task ExecCapabilityTools_WhenOff_AreBlocked_WithoutTouchingTheEngineOrPrompting(string tool)
    {
        var h = _Build(ConsentOutcome.Approved, allowExec: false);

        var json = JsonNode.Parse(tool switch
        {
            "exec" => await h.Tools.Exec(Session, "web", "ls -la"),
            "build_image" => await h.Tools.BuildImage(Session, "/ctx", "myapp:latest"),
            _ => await h.Tools.Cp(Session, "web:/app/log", "/tmp/log"),
        });

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("settings", json["error"]!.GetValue<string>());
        Assert.Empty(h.Engine.Execs);
        Assert.Empty(h.Docker.Calls);
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
}
