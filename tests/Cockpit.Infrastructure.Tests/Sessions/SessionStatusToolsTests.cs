using System.Text.Json;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-128: set_status keys on the transport-verified pane, not the agent-declared <c>session</c>, so an agent cannot
/// spoof or clear another session's statusline by naming its id (confused deputy) — the AC-89 pattern the terminal
/// tools already hold.
/// </summary>
public class SessionStatusToolsTests
{
    private static ISessionLabelSink _Labels()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        return sink;
    }

    // A workspace snapshot where `paneId` delivers at turn start — the strongest reachability route, so start_run
    // should carry no `unreachable` warning against it.
    private static IWorkspaceAgentGateway _WorkspacesReachableAtTurnStart(string paneId)
    {
        var workspaces = Substitute.For<IWorkspaceAgentGateway>();
        workspaces.GetWorkspaceSnapshotAsync(paneId).Returns(new WorkspaceAgentSnapshot(
            "workspace-1", [new WorkspaceAgentPane(paneId, "session", null, "", DeliversAtTurnStart: true)]));
        return workspaces;
    }

    // A pane with no turn-start delivery, no prior contact and no wake consent — `_ReachableVia` classifies this as
    // `operatorOnly`, the one case start_run must warn about.
    private static (IWorkspaceAgentGateway Workspaces, IWorkspaceAgentCoordinator Coordinator) _UnreachableSetup(string paneId)
    {
        var workspaces = Substitute.For<IWorkspaceAgentGateway>();
        workspaces.GetWorkspaceSnapshotAsync(paneId).Returns(new WorkspaceAgentSnapshot(
            "workspace-1", [new WorkspaceAgentPane(paneId, "session", null, "", DeliversAtTurnStart: false)]));

        var coordinator = Substitute.For<IWorkspaceAgentCoordinator>();
        coordinator.LastContactUtc(paneId).Returns((DateTimeOffset?)null);
        coordinator.HasWakeConsent(paneId).Returns(false);

        return (workspaces, coordinator);
    }

    private static SessionStatusTools _Tools(
        ISessionLabelSink? labels = null,
        ITrackedCommandRunner? runner = null,
        RunTracker? tracker = null,
        IWorkspaceAgentGateway? workspaces = null,
        IWorkspaceAgentCoordinator? coordinator = null,
        IAgentMessageInbox? inbox = null,
        IProjectJobHistory? jobHistory = null) =>
        new(
            labels ?? _Labels(),
            runner ?? Substitute.For<ITrackedCommandRunner>(),
            tracker ?? new RunTracker(),
            workspaces ?? Substitute.For<IWorkspaceAgentGateway>(),
            coordinator ?? Substitute.For<IWorkspaceAgentCoordinator>(),
            inbox ?? Substitute.For<IAgentMessageInbox>(),
            jobHistory);

    /// <summary>
    /// AC-490 criterion 3: the summary is the agent's claim, kept against the caller's own run and nowhere else. A
    /// session that was not started from a job is refused rather than written to a run it does not have — and the
    /// verified pane decides which run that is, never the id the agent names (the AC-128 rule set_status holds).
    /// </summary>
    [Fact]
    public async Task ReportJobProgress_RecordsTheAgentsClaimAgainstTheCallersRun_AndRefusesASessionWithoutOne()
    {
        var path = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"), "job-history.jsonl");
        var history = new ProjectJobHistoryLog(path, NullLogger<ProjectJobHistoryLog>.Instance);
        await history.RecordAsync(new ProjectJobRunEvent("job-pane", "p1", "j1", DateTimeOffset.Now.AddMinutes(-5), ProjectJobRunEventKind.Started));
        var tools = _Tools(jobHistory: history);

        try
        {
            // The agent names another pane's id; the report still lands on the verified caller's run.
            McpRequestContext.Set("job-pane");
            var accepted = JsonDocument.Parse(await tools.ReportJobProgressAsync("14 of 22 invoices read, 3 need a human", "other-pane")).RootElement;
            Assert.True(accepted.GetProperty("ok").GetBoolean());

            var run = Assert.Single(await history.ReadRecentRunsAsync());
            Assert.Equal("job-pane", run.PaneId);
            Assert.Equal("14 of 22 invoices read, 3 need a human", run.AgentSummary);
            Assert.NotNull(run.LastReportedAt);

            // A later report replaces the earlier one in what the run shows; the trail keeps both lines.
            await tools.ReportJobProgressAsync("22 of 22 invoices read");
            Assert.Equal("22 of 22 invoices read", Assert.Single(await history.ReadRecentRunsAsync()).AgentSummary);

            // A pane with no run: refused, and the trail is not touched.
            McpRequestContext.Set("plain-pane");
            var refused = JsonDocument.Parse(await tools.ReportJobProgressAsync("did some things")).RootElement;
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Contains("not started from a project job", refused.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Equal(3, (await history.ReadRecentAsync(limit: int.MaxValue)).Count);
        }
        finally
        {
            McpRequestContext.Set(null);
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task SetStatus_KeysOnTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        var sink = _Labels();
        var tools = _Tools(labels: sink);

        McpRequestContext.Set("verified-pane");
        try
        {
            // The agent spoofs another session's id in the tool argument.
            await tools.SetStatusAsync("pwned", "victim-pane");

            // The status lands on the verified caller, never the spoofed id.
            await sink.Received(1).SetStatuslineAsync("verified-pane", "pwned");
            await sink.DidNotReceive().SetStatuslineAsync("victim-pane", Arg.Any<string>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // The name travels on the same tool call, so it inherits the same hazard: without this it would be a second way
    // to reach a session you do not own, and a renamed session is more disruptive than a rewritten status line.
    [Fact]
    public async Task SetStatus_ProposesTheNameToTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        var sink = _Labels();
        sink.SuggestNameAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var tools = _Tools(labels: sink);

        McpRequestContext.Set("verified-pane");
        try
        {
            await tools.SetStatusAsync("pwned", "victim-pane", "pwned-name");

            await sink.Received(1).SuggestNameAsync("verified-pane", "pwned-name");
            await sink.DidNotReceive().SuggestNameAsync("victim-pane", Arg.Any<string>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // AC-1028: `session` is only a fallback for the unverified in-process path — on the transport-verified path it
    // is not needed at all, so omitting it must succeed rather than throw a marshalling error.
    [Fact]
    public async Task SetStatus_SucceedsWithoutSession_OnTheVerifiedPath()
    {
        var sink = _Labels();
        var tools = _Tools(labels: sink);

        McpRequestContext.Set("verified-pane");
        try
        {
            await tools.SetStatusAsync("AC-1028");

            await sink.Received(1).SetStatuslineAsync("verified-pane", "AC-1028");
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // Off the verified path (the in-process tool loop / tests) there is no middleware to trust, so a caller that
    // gives no `session` either gets a readable error, not an exception or a silently ignored call.
    [Fact]
    public async Task SetStatus_ReturnsAReadableError_WhenUnverifiedAndNoSessionGiven()
    {
        var sink = _Labels();
        var tools = _Tools(labels: sink);

        var result = await tools.SetStatusAsync("AC-1028");

        Assert.Contains("session", result, StringComparison.OrdinalIgnoreCase);
        await sink.DidNotReceiveWithAnyArgs().SetStatuslineAsync(default!, default!);
    }

    // AC-1094 criterion 1: the call returns a run id before the command itself has finished — the whole point is
    // that the caller does not wait on it.
    [Fact]
    public async Task StartRun_ReturnsARunId_BeforeTheCommandFinishes()
    {
        var neverCompletes = new TaskCompletionSource<TrackedRunResult>();
        var runner = Substitute.For<ITrackedCommandRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(neverCompletes.Task);
        var tools = _Tools(runner: runner, workspaces: _WorkspacesReachableAtTurnStart("caller-pane"));

        McpRequestContext.Set("caller-pane");
        try
        {
            var reply = await tools.StartRunAsync("/tmp", "dotnet", ["test"]);

            using var document = JsonDocument.Parse(reply);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("runId").GetString()));
        }
        finally
        {
            McpRequestContext.Set(null);
            neverCompletes.TrySetResult(new TrackedRunResult(0, "", "", TimeSpan.Zero, false));
        }
    }

    [Fact]
    public async Task StartRun_StopAsync_CancelsTheTokenHandedToTheRunner()
    {
        var runnerStarted = new TaskCompletionSource();
        var neverCompletes = new TaskCompletionSource<TrackedRunResult>();
        CancellationToken handedToRunner = default;
        var runner = Substitute.For<ITrackedCommandRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                handedToRunner = callInfo.ArgAt<CancellationToken>(5);
                runnerStarted.TrySetResult();
                return neverCompletes.Task;
            });
        var tracker = new RunTracker();
        var tools = _Tools(runner: runner, tracker: tracker, workspaces: _WorkspacesReachableAtTurnStart("caller-pane"));

        McpRequestContext.Set("caller-pane");
        try
        {
            await tools.StartRunAsync("/tmp", "dotnet", ["test"]);
            await runnerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var activity = Assert.Single(tracker.Snapshot());
            var exception = await Record.ExceptionAsync(activity.StopAsync);

            Assert.Multiple(
                () => Assert.Null(exception),
                () => Assert.True(handedToRunner.IsCancellationRequested));
        }
        finally
        {
            McpRequestContext.Set(null);
            neverCompletes.TrySetResult(new TrackedRunResult(0, "", "", TimeSpan.Zero, false));
        }
    }

    // AC-1094 criterion 2: on completion the verdict reaches the caller's own inbox without it doing anything —
    // proven here by awaiting the delivery itself rather than polling for it.
    [Fact]
    public async Task StartRun_DeliversTheVerdictToTheCallersInbox_OnCompletion()
    {
        var runner = Substitute.For<ITrackedCommandRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TrackedRunResult(0, "all green", "", TimeSpan.FromSeconds(2), false));

        var delivered = new TaskCompletionSource();
        var inbox = Substitute.For<IAgentMessageInbox>();
        inbox.Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                delivered.TrySetResult();
                return new AgentMessageDelivery(AgentMessageDeliveryOutcome.Delivered, null);
            });

        var tools = _Tools(runner: runner, workspaces: _WorkspacesReachableAtTurnStart("caller-pane"), inbox: inbox);

        McpRequestContext.Set("caller-pane");
        try
        {
            await tools.StartRunAsync("/tmp", "dotnet", ["test"]);
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            inbox.Received(1).Deliver(Arg.Any<string>(), "caller-pane", Arg.Any<string>(), Arg.Is<string>(body => body.Contains("Passed")));
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // AC-1094 criterion 3: a caller with no turn-start delivery and no wake consent is told so in the startcall
    // itself — not left to discover it only once the run is already done and nothing came back.
    [Fact]
    public async Task StartRun_WarnsInTheReply_WhenNothingWillBringTheVerdictBackOnItsOwn()
    {
        var (workspaces, coordinator) = _UnreachableSetup("caller-pane");
        var runner = Substitute.For<ITrackedCommandRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<TrackedRunResult>().Task);
        var tools = _Tools(runner: runner, workspaces: workspaces, coordinator: coordinator);

        McpRequestContext.Set("caller-pane");
        try
        {
            var reply = await tools.StartRunAsync("/tmp", "dotnet", ["test"]);

            using var document = JsonDocument.Parse(reply);
            var unreachable = document.RootElement.GetProperty("unreachable");
            Assert.NotEqual(JsonValueKind.Null, unreachable.ValueKind);
            Assert.Contains("run_status", unreachable.GetString());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task StartRun_NoWarning_WhenReachableAtTurnStart()
    {
        var runner = Substitute.For<ITrackedCommandRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<TrackedRunResult>().Task);
        var tools = _Tools(runner: runner, workspaces: _WorkspacesReachableAtTurnStart("caller-pane"));

        McpRequestContext.Set("caller-pane");
        try
        {
            var reply = await tools.StartRunAsync("/tmp", "dotnet", ["test"]);

            using var document = JsonDocument.Parse(reply);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("unreachable").ValueKind);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    [Fact]
    public async Task RunStatus_ReturnsAReadableError_ForAnUnknownRunId()
    {
        var tools = _Tools();

        var reply = tools.RunStatus("never-started");

        using var document = JsonDocument.Parse(reply);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
    }

    // AC-1094 criterion 4: the recovery path — the same verdict the inbox delivery may have missed, retrievable by
    // run id alone, without starting or restarting anything.
    [Fact]
    public async Task RunStatus_ReturnsTheVerdict_ForACompletedRun()
    {
        var runner = Substitute.For<ITrackedCommandRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TrackedRunResult(1, "", "boom", TimeSpan.FromSeconds(3), false));

        var delivered = new TaskCompletionSource();
        var inbox = Substitute.For<IAgentMessageInbox>();
        inbox.Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                delivered.TrySetResult();
                return new AgentMessageDelivery(AgentMessageDeliveryOutcome.Delivered, null);
            });

        var tools = _Tools(runner: runner, workspaces: _WorkspacesReachableAtTurnStart("caller-pane"), inbox: inbox);

        McpRequestContext.Set("caller-pane");
        string runId;
        try
        {
            var started = await tools.StartRunAsync("/tmp", "dotnet", ["test"]);
            using var document = JsonDocument.Parse(started);
            runId = document.RootElement.GetProperty("runId").GetString()!;

            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            McpRequestContext.Set(null);
        }

        var reply = tools.RunStatus(runId);
        using var status = JsonDocument.Parse(reply);
        Assert.True(status.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Failed", status.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(1, status.RootElement.GetProperty("exitCode").GetInt32());
    }
}
