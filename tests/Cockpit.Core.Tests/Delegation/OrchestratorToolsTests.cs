using System.Text.Json;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.ModelIntelligence;
using Cockpit.Core.Delegation;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.ModelIntelligence;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The MCP tool surface (#67). It is deliberately thin — the rules live in the engine — so what matters here is
/// that it reports refusals and unknown tasks honestly rather than returning something that reads like success.
/// </summary>
public class OrchestratorToolsTests
{
    internal static ModelPictureReader PictureReader() =>
        new(Substitute.For<IModelIntelligence>(), Substitute.For<IProviderModelIntelligence>(), Substitute.For<ISessionProfileStore>());

    [Fact]
    public async Task DelegateTask_WhenTheEngineRefuses_TellsTheAgentWhy()
    {
        // A silent failure is the worst outcome: the agent would believe the work is under way and wait for a
        // result that is never coming.
        var delegation = Substitute.For<IDelegationService>();
        delegation.DelegateAsync(Arg.Any<DelegationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<DelegatedTaskView>>(_ => throw new DelegationRejectedException("Profile 'private' is not available as a delegation target."));
        var tools = new OrchestratorTools(delegation, PictureReader());

        var json = await tools.DelegateTaskAsync("private", "do work", null, null, null, null, null, CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("rejected").GetBoolean());
        Assert.Contains("not available as a delegation target", document.RootElement.GetProperty("reason").GetString());
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
        var tools = new OrchestratorTools(delegation, PictureReader());

        var json = await tools.DelegateTaskAsync("local", "bulk work", null, null, null, null, null, CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("queued").GetBoolean());
        var note = document.RootElement.GetProperty("note").GetString();
        Assert.Contains("Do not call delegate_task again", note);
        Assert.Contains("get_task_status", note);
        Assert.Equal("task-2", document.RootElement.GetProperty("task").GetProperty("TaskId").GetString());
    }

    [Fact]
    public void GetTaskOutput_ReturnsTheCursorToPollWith()
    {
        var delegation = Substitute.For<IDelegationService>();
        delegation.GetOutput("task-1", 0).Returns((
            new List<SessionEvent> { new AssistantTextCompleted { SessionId = "s", Text = "working on it" } },
            1,
            false));
        var tools = new OrchestratorTools(delegation, PictureReader());

        var json = tools.GetTaskOutput("task-1");

        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("cursor").GetInt32());
        Assert.False(document.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal("working on it", document.RootElement.GetProperty("events")[0].GetProperty("text").GetString());
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
