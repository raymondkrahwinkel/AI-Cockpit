using System.Text.Json;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Delegation;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The MCP tool surface (#67). It is deliberately thin — the rules live in the engine — so what matters here is
/// that it reports refusals and unknown tasks honestly rather than returning something that reads like success.
/// </summary>
public class OrchestratorToolsTests
{
    [Fact]
    public async Task DelegateTask_WhenTheEngineRefuses_TellsTheAgentWhy()
    {
        // A silent failure is the worst outcome: the agent would believe the work is under way and wait for a
        // result that is never coming.
        var delegation = Substitute.For<IDelegationService>();
        delegation.DelegateAsync(Arg.Any<DelegationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<DelegatedTaskView>>(_ => throw new DelegationRejectedException("Profile 'private' is not available as a delegation target."));
        var tools = new OrchestratorTools(delegation);

        var json = await tools.DelegateTaskAsync("private", "do work", null, null, null, null, CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("rejected").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("reason").GetString().Should().Contain("not available as a delegation target");
    }

    [Fact]
    public async Task DelegateTask_ReturnsTheTaskIdImmediately_RatherThanWaitingForTheAnswer()
    {
        // Delegation is asynchronous by design: a sub-agent can run for minutes, which no MCP call should sit
        // through.
        var delegation = Substitute.For<IDelegationService>();
        delegation.DelegateAsync(Arg.Any<DelegationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_View("task-1", DelegatedTaskStatus.Running));
        var tools = new OrchestratorTools(delegation);

        var json = await tools.DelegateTaskAsync("local", "summarise", null, null, null, null, CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("TaskId").GetString().Should().Be("task-1");
    }

    [Fact]
    public async Task DelegateTask_WhenQueued_SaysItIsAcceptedAndToPollRatherThanRetry()
    {
        // A bare "Queued" reads to a model like a failure: it re-sends the same work and the tasks pile up behind
        // a profile that runs them one at a time anyway (seen live with MaxConcurrent = 1). The answer has to say
        // that the work is in hand and that polling — not delegating again — is the next move.
        var delegation = Substitute.For<IDelegationService>();
        delegation.DelegateAsync(Arg.Any<DelegationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_View("task-2", DelegatedTaskStatus.Queued));
        var tools = new OrchestratorTools(delegation);

        var json = await tools.DelegateTaskAsync("local", "bulk work", null, null, null, null, CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("queued").GetBoolean().Should().BeTrue();
        var note = document.RootElement.GetProperty("note").GetString();
        note.Should().Contain("Do not call delegate_task again");
        note.Should().Contain("get_task_status");
        document.RootElement.GetProperty("task").GetProperty("TaskId").GetString().Should().Be("task-2");
    }

    [Fact]
    public void GetTaskResult_OnAnUnknownTask_SaysSo()
    {
        var delegation = Substitute.For<IDelegationService>();
        delegation.GetTask("nope").Returns((DelegatedTaskView?)null);
        var tools = new OrchestratorTools(delegation);

        var json = tools.GetTaskResult("nope");

        json.Should().Contain("No task");
    }

    [Fact]
    public void GetTaskResult_ReturnsTheAnswer_NotTheWholeTranscript()
    {
        // Keeping the sub-agent's transcript out of the caller's context is the point of delegating in the first
        // place; the events are available separately for a caller that actually wants to watch.
        var delegation = Substitute.For<IDelegationService>();
        delegation.GetTask("task-1").Returns(_View("task-1", DelegatedTaskStatus.Completed, result: "the summary"));
        var tools = new OrchestratorTools(delegation);

        var json = tools.GetTaskResult("task-1");

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("result").GetString().Should().Be("the summary");
        document.RootElement.GetProperty("status").GetString().Should().Be("Completed");
    }

    [Fact]
    public void GetTaskOutput_ReturnsTheCursorToPollWith()
    {
        var delegation = Substitute.For<IDelegationService>();
        delegation.GetOutput("task-1", 0).Returns((
            new List<SessionEvent> { new AssistantTextCompleted { SessionId = "s", Text = "working on it" } },
            1,
            false));
        var tools = new OrchestratorTools(delegation);

        var json = tools.GetTaskOutput("task-1");

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("cursor").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("done").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("events")[0].GetProperty("text").GetString().Should().Be("working on it");
    }

    // AC-100/AC-113: a tool result carries its content back to the poller — above all a gate denial / tool error,
    // which was previously null-text and left a caller unable to see *why* a tool (e.g. write_file) failed.
    [Fact]
    public void GetTaskOutput_SurfacesAToolResultsContent_AndMarksAnError()
    {
        var delegation = Substitute.For<IDelegationService>();
        delegation.GetOutput("task-1", 0).Returns((
            new List<SessionEvent>
            {
                new ToolResult { SessionId = "s", ToolUseId = "t1", Content = "wrote 1 file", IsError = false },
                new ToolResult { SessionId = "s", ToolUseId = "t2", Content = "write_file was blocked", IsError = true },
            },
            2,
            true));
        var tools = new OrchestratorTools(delegation);

        var json = tools.GetTaskOutput("task-1");

        using var document = JsonDocument.Parse(json);
        var events = document.RootElement.GetProperty("events");
        // A normal tool result surfaces its content verbatim (was null before the fix)...
        events[0].GetProperty("text").GetString().Should().Be("wrote 1 file");
        // ...and an error result is marked so a poll can tell a denial from a normal return.
        events[1].GetProperty("text").GetString().Should().Be("[error] write_file was blocked");
    }

    private static DelegatedTaskView _View(string id, DelegatedTaskStatus status, string? result = null) => new(
        id,
        ProfileLabel: "local",
        Label: null,
        TaskType: null,
        status,
        CreatedAt: DateTimeOffset.Now,
        StartedAt: DateTimeOffset.Now,
        FinishedAt: null,
        TurnCount: 0,
        result,
        Error: null);
}
