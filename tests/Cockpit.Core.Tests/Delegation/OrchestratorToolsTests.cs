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
        delegation.DelegateAsync(Arg.Any<DelegationRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<DelegatedTaskView>>(_ => throw new DelegationRejectedException("Profile 'private' is not available as a delegation target."));
        var tools = new OrchestratorTools(delegation);

        var json = await tools.DelegateTaskAsync("private", "do work", null, null, null, CancellationToken.None);

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
        delegation.DelegateAsync(Arg.Any<DelegationRequest>(), Arg.Any<CancellationToken>())
            .Returns(_View("task-1", DelegatedTaskStatus.Running));
        var tools = new OrchestratorTools(delegation);

        var json = await tools.DelegateTaskAsync("local", "summarise", null, null, null, CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("TaskId").GetString().Should().Be("task-1");
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
