using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Events;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.BackendApi;

// AC-1386's acceptance on the real door (`BackendApiDoorTests._Door`): the session routes over HTTPS with connect keys,
// on the recording gateways the node tool tests use, so what a route reaches is what the node tools would reach.
public sealed class SessionsEndpointsTests
{
    // The labels `NodeSessionMcpToolsTests.StubProfileStore` and `RecordingReadGateway` know.
    private const string Profile = "Laptop Sonnet";

    private const string Project = "project-allowed";

    private static readonly NodeCaller Operator = new("testtest", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);

    private static readonly ConnectKeyScope OneProject = new() { AllowAllProjects = false, AllowedProjectIds = [Project] };

    // Criterion 2: a start outside the key's projects is a 403 that names the scope; inside it, a 201 with the pane.
    [Theory]
    [InlineData("project-elsewhere", HttpStatusCode.Forbidden, "The scope of this connect key does not include the project")]
    [InlineData(Project, HttpStatusCode.Created, "\"paneId\":\"pane-new\"")]
    public async Task StartingASession_IsHeldToTheKeysProjects(string project, HttpStatusCode expected, string bodyPart)
    {
        await using var door = new BackendApiDoorTests._Door();
        var verifier = await door.StartAsync();
        var key = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator, scope: OneProject);

        var answer = await door.SendAsync(HttpMethod.Post, "/api/v1/sessions", key.Secret, JsonSerializer.Serialize(new { profile = Profile, projectId = project }));

        Assert.Equal(expected, answer.Status);
        Assert.Contains(bodyPart, answer.Body, StringComparison.Ordinal);
    }

    // Criterion 3: answering or switching into an auto-accepting mode takes the mayAnswerPermissions grant.
    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden, NodeSessionMcpTools.PermissionsRefusal, "\"mayAnswerPermissions\":false")]
    [InlineData(true, HttpStatusCode.OK, "\"toolUseId\":\"tool-1\"", "\"mayAnswerPermissions\":true")]
    public async Task AnsweringAPermission_TakesTheGrant(bool mayAnswerPermissions, HttpStatusCode expected, string answerPart, string whoamiPart)
    {
        await using var door = new BackendApiDoorTests._Door();
        var verifier = await door.StartAsync();
        door.ReadGateway.Sessions.Add(_Row("pane-in", Project));
        var handle = Substitute.For<ISessionHandle>();
        var control = Substitute.For<ISessionControl>();
        handle.PaneId.Returns("pane-in");
        handle.Control.Returns(control);
        handle.UseControlAsync(Arg.Any<Func<ISessionControl, Task>>()).Returns(async call =>
        {
            await call.Arg<Func<ISessionControl, Task>>()(control);
            return true;
        });
        door.Sessions.Register(handle);
        var key = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator, scope: new ConnectKeyScope { MayAnswerPermissions = mayAnswerPermissions });

        var answer = await door.SendAsync(HttpMethod.Post, "/api/v1/sessions/pane-in/permissions/tool-1", key.Secret, """{"allow":true}""");
        var mode = await door.SendAsync(HttpMethod.Post, "/api/v1/sessions/pane-in/permission-mode", key.Secret, """{"mode":"acceptEdits"}""");
        var bypass = await door.SendAsync(HttpMethod.Post, "/api/v1/sessions/pane-in/permission-mode", key.Secret, """{"mode":"bypassPermissions"}""");

        var whoami = await door.SendAsync(HttpMethod.Get, "/api/v1/whoami", key.Secret, null);

        Assert.Equal(expected, answer.Status);
        Assert.Equal(expected, mode.Status);
        Assert.Equal(HttpStatusCode.BadRequest, bypass.Status);

        // AC-1469: /whoami tells the desktop whether to draw the buttons; a direct answer without the grant gets the one refusal.
        Assert.Contains(whoamiPart, whoami.Body, StringComparison.Ordinal);
        Assert.Contains(answerPart, answer.Body, StringComparison.Ordinal);
    }

    // Seeing is reaching: a pane outside the key's projects is not found on any route, and no gateway call reaches it.
    [Theory]
    [InlineData("GET", "/api/v1/sessions/pane-out/transcript", null)]
    [InlineData("POST", "/api/v1/sessions/pane-out/prompt", """{"text":"go"}""")]
    [InlineData("DELETE", "/api/v1/sessions/pane-out", null)]
    [InlineData("POST", "/api/v1/sessions/pane-out/permissions/tool-1", """{"allow":true}""")]
    [InlineData("POST", "/api/v1/sessions/pane-out/interrupt", null)]
    [InlineData("POST", "/api/v1/sessions/pane-out/model", """{"model":"sonnet"}""")]
    [InlineData("POST", "/api/v1/sessions/pane-out/permission-mode", """{"mode":"acceptEdits"}""")]
    [InlineData("POST", "/api/v1/sessions/pane-out/queue", """{"command":"submit","wireId":"queue-1","text":"go"}""")]
    public async Task APaneOutsideTheScope_IsNotFound(string method, string path, string? body)
    {
        await using var door = new BackendApiDoorTests._Door();
        var verifier = await door.StartAsync();
        door.ReadGateway.Sessions.Add(_Row("pane-out", "project-elsewhere"));
        var handle = Substitute.For<ISessionHandle>();
        var control = Substitute.For<ISessionControl>();
        handle.PaneId.Returns("pane-out");
        handle.Control.Returns(control);
        handle.UseControlAsync(Arg.Any<Func<ISessionControl, Task>>()).Returns(async call =>
        {
            await call.Arg<Func<ISessionControl, Task>>()(control);
            return true;
        });
        door.Sessions.Register(handle);
        var key = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator, scope: OneProject);

        var answer = await door.SendAsync(new HttpMethod(method), path, key.Secret, body);

        Assert.Equal(HttpStatusCode.NotFound, answer.Status);
        Assert.DoesNotContain(door.AgentGateway.Calls, call => call.Contains("pane-out", StringComparison.Ordinal));
        Assert.Empty(control.ReceivedCalls());
    }

    [Fact]
    public async Task TheSessionList_ShowsOnlyTheKeysSessions()
    {
        await using var door = new BackendApiDoorTests._Door();
        var verifier = await door.StartAsync();
        door.ReadGateway.Sessions.Add(_Row("pane-in", Project));
        door.ReadGateway.Sessions.Add(_Row("pane-out", "project-elsewhere"));
        door.Providers.Register(new SessionProviderRegistration(
            ClaudePluginProfile.ProviderId,
            "Claude",
            _ => Substitute.For<IPluginSessionDriverFactory>(),
            new PluginSessionCapabilities(false, false, false))
        {
            UsageSignals =
            [
                new PluginUsageSignal("context", "ctx", PluginUsageSignalKind.Fill, 50)
                {
                    Description = "Context window",
                },
            ],
        });
        var key = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator, scope: OneProject);

        var answer = await door.SendAsync(HttpMethod.Get, "/api/v1/sessions", key.Secret);
        var sessions = JsonNode.Parse(answer.Body)?["sessions"]?.AsArray() ?? [];
        var panes = sessions.Select(session => session?["paneId"]?.GetValue<string>());
        var visible = Assert.Single(sessions);
        var usage = Assert.Single(visible?["usageSignals"]?.AsArray() ?? []);

        Assert.Equal(["pane-in"], panes);
        Assert.Equal("claude", visible?["providerId"]?.GetValue<string>());
        Assert.Equal("context", usage?["key"]?.GetValue<string>());
        Assert.Equal("fill", usage?["kind"]?.GetValue<string>());
        Assert.Equal("Context window", usage?["description"]?.GetValue<string>());
    }

    // Criterion 4: a prompt to the assistant over the API reaches it, is audited and leaves the assistant free; the same
    // holdsAssistant key on the MCP door does take the line.
    [Fact]
    public async Task APromptToTheAssistant_IsAuditedAndNeverHoldsIt_WhereTheSameKeyOnTheMcpDoorDoes()
    {
        await using var door = new BackendApiDoorTests._Door();
        var verifier = await door.StartAsync();
        var assistant = Substitute.For<ISessionHandle>();
        assistant.PaneId.Returns("assistant-pane");
        assistant.SubmitPromptWhenReadyAsync("hello").Returns(true);
        door.Sessions.RegisterAssistant(assistant);
        var holding = await verifier.IssueAsync("holding", ConnectKeyCapability.Operate, 30, Operator, holdsAssistant: true);

        var answer = await door.SendAsync(HttpMethod.Post, "/api/v1/assistant/prompt", holding.Secret, """{"text":"hello"}""");
        var afterApi = door.Presence.Current;
        using var mcp = await door.InitializeMcpAsync(holding.Secret);

        Assert.Equal(new BackendApiDoorTests._Answer(HttpStatusCode.OK, """{"paneId":"assistant-pane","delivered":true}"""), answer);
        Assert.Null(afterApi);
        Assert.Equal("holding", door.Presence.Current?.Name);
        Assert.Contains("api:assistant_prompt", await File.ReadAllTextAsync(door.AuditPath), StringComparison.Ordinal);
    }

    // Criterion 6 (corrected): two upserts whose seqs were drawn in one order reach the log in the other, as two threads
    // racing for its gate do. The event ids still only rise, so a resume after the last one misses nothing, and each
    // row carries its upsert's own seq and its pane in the data. A registry change is announced first.
    [Fact]
    public async Task TheBridge_PreservesEmissionOrder_AndKeepsRowUpsertSeqInTheData()
    {
        var registry = new SessionRegistry();
        var log = new BackendEventLog();
        var bridge = new SessionEventsBridge(registry, log);
        await bridge.StartAsync(CancellationToken.None);
        var session = Substitute.For<ISessionHandle>();
        session.PaneId.Returns("pane-a");
        var control = Substitute.For<ISessionControl>();
        var releaseControlRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdNextControlRead = true;
        session.UseControlAsync(Arg.Any<Func<ISessionControl, Task>>()).Returns(async call =>
        {
            if (holdNextControlRead)
            {
                holdNextControlRead = false;
                await releaseControlRead.Task;
            }

            await call.Arg<Func<ISessionControl, Task>>()(control);
            return true;
        });
        IReadOnlyList<QueuedPrompt> queue = [];
        control.Queue.Returns(_ => queue);
        session.Control.Returns(control);
        var first = _Upsert(SessionEventSequence.Next(), "row-1");
        var second = _Upsert(SessionEventSequence.Next(), "row-2");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = log.ReadFromAsync(0, stop.Token).Take(8).ToListAsync(stop.Token);
        // AC-1388: a tool's output past the row's budget does not reach the stream by the tool event either.
        var output = new string('x', 200 * 1024);

        registry.Register(session);
        session.RowUpserted += Raise.Event<Action<TranscriptRowUpsert>>(second);
        session.LiveStateChanged += Raise.Event<Action<SessionLiveState>>(SessionLiveState.None);
        session.RowUpserted += Raise.Event<Action<TranscriptRowUpsert>>(first);
        releaseControlRead.SetResult(true);
        session.ToolActivityProduced += Raise.Event<Action<SessionToolCall>>(new SessionToolCall("pane-a", "Bash", "{}", output, false));
        queue = [new QueuedPrompt("later", [], wireId: "queue-1")];
        control.QueueChanged += Raise.Event<System.Collections.Specialized.NotifyCollectionChangedEventHandler>(
            control,
            new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        session.TurnEnded += Raise.Event<Action<SessionTurnEnd>>(new SessionTurnEnd(false, false, false, null, "success", null));
        control.ReadUsageStatus(null).Returns(new SessionStatusFeed(42, []));
        control.UsageCatchUpDue += Raise.Event<Action>();
        var events = await read;
        await bridge.StopAsync(CancellationToken.None);

        Assert.Equal(["sessions-changed", "row", "live-state", "row", "tool", "queue", "turn-ended", "usage"], events.Select(evt => evt.Kind));
        Assert.Equal(events.Select(evt => evt.Seq).Order(), events.Select(evt => evt.Seq));
        Assert.Equal(8, events.Select(evt => evt.Seq).Distinct().Count());
        Assert.True(events.Single(evt => evt.Kind == "tool").Data.GetProperty("Call").GetProperty("ResultContent").GetString()?.Length <= ToolOutputBudget.Clamp(output).Length);
        Assert.Equal([(second.Seq, "pane-a"), (first.Seq, "pane-a")], events.Where(evt => evt.Kind == "row").Select(evt => (evt.Data.GetProperty("Seq").GetInt64(), evt.Data.GetProperty("PaneId").GetString())));
        var queueEvent = events.Single(evt => evt.Kind == "queue");
        Assert.Equal("queue-1", queueEvent.Data.GetProperty("Queue")[0].GetProperty("WireId").GetString());
        Assert.Equal("later", queueEvent.Data.GetProperty("Queue")[0].GetProperty("Text").GetString());
        Assert.Equal("success", events.Single(evt => evt.Kind == "turn-ended").Data.GetProperty("End").GetProperty("Subtype").GetString());
        Assert.Equal(42, events.Single(evt => evt.Kind == "usage").Data.GetProperty("UsageStatus").GetProperty("ContextUsedPercent").GetDouble());
    }

    private static TranscriptRowUpsert _Upsert(long seq, string id) =>
        new(seq, 1, new TranscriptSnapshotEntry(id, "AssistantText", "hi", null, null, null, null, false, DateTimeOffset.UnixEpoch));

    private static AssistantSessionRow _Row(string paneId, string project) =>
        new(paneId, paneId, Profile, "", null, null, ProjectId: project);
}
