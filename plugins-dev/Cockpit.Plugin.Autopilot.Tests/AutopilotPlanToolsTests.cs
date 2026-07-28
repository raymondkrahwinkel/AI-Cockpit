using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Tracking;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The CEO plan-emit tool's parsing (AC-174): a well-formed steps array builds the plan; a malformed or half-formed one
/// is turned down with a clear error rather than producing an unrunnable plan. The pane-scoping half is covered where
/// the tool is wired (it uses the same CurrentMcpCallerPaneId gate as AutopilotMcpTools).
/// </summary>
public class AutopilotPlanToolsTests
{
    [Fact]
    public void TryParseSteps_BuildsSteps_MappingProfileModelAndHard()
    {
        const string json = """
            [
              {"id":"1","title":"Code","description":"do it","profile":"Claude","model":"Sonnet","brief":"b","acceptance":"a","hard":false},
              {"id":"2","title":"Security","description":"review","profile":"Claude","model":"Opus","brief":"b","hard":true}
            ]
            """;

        AutopilotPlanTools.TryParseSteps(json, out var steps, out var error).Should().BeTrue();
        error.Should().BeNull();
        steps.Should().HaveCount(2);
        steps[0].Should().BeEquivalentTo(new
        {
            Id = "1", Title = "Code", ProfileLabel = "Claude", Model = "Sonnet", Mode = GateMode.Skip, Status = AutopilotStepStatus.Pending,
        });
        steps[1].Mode.Should().Be(GateMode.Hard);
    }

    [Fact]
    public void TryParseSteps_ReadsReviewGate_AndCoercesItToAHardGate_RegardlessOfTheHardFlag()
    {
        // AC-434: a review gate is definitionally required — the CEO does not also have to remember 'hard'.
        const string json = """
            [
              {"id":"1","title":"Code review","profile":"Claude","brief":"b","hard":false,"reviewGate":true},
              {"id":"2","title":"PR","profile":"Claude","brief":"b"}
            ]
            """;

        AutopilotPlanTools.TryParseSteps(json, out var steps, out _).Should().BeTrue();
        steps[0].IsReviewGate.Should().BeTrue();
        steps[0].Mode.Should().Be(GateMode.Hard);
        steps[1].IsReviewGate.Should().BeFalse();
    }

    [Fact]
    public void TryParseSteps_KeepsTheMinimalMcpSetPerStep_DroppingBlanks()
    {
        const string json = """
            [
              {"id":"2","title":"Visual verify","profile":"Claude","brief":"b","mcp":["cockpit-verify","  "]},
              {"id":"3","title":"Code review","profile":"Claude","brief":"b"}
            ]
            """;

        AutopilotPlanTools.TryParseSteps(json, out var steps, out _).Should().BeTrue();
        steps[0].McpServers.Should().Equal("cockpit-verify");
        steps[1].McpServers.Should().BeEmpty();
    }

    [Fact]
    public void TryParseSteps_TreatsAMissingModel_AsNull()
    {
        const string json = """[{"id":"5","title":"Conventions","profile":"Qwen (local)","brief":"b"}]""";

        AutopilotPlanTools.TryParseSteps(json, out var steps, out _).Should().BeTrue();
        steps[0].Model.Should().BeNull();
    }

    [Fact]
    public void TryParseSteps_ReadsAgentCount_DefaultingToOne_AndClampingBelowOne()
    {
        const string json = """
            [
              {"id":"1","title":"Code","profile":"Claude","brief":"b","agents":3},
              {"id":"2","title":"Review","profile":"Claude","brief":"b"},
              {"id":"3","title":"PR","profile":"Claude","brief":"b","agents":0}
            ]
            """;

        AutopilotPlanTools.TryParseSteps(json, out var steps, out _).Should().BeTrue();
        steps[0].AgentCount.Should().Be(3);
        steps[1].AgentCount.Should().Be(1);
        steps[2].AgentCount.Should().Be(1);
    }

    [Fact]
    public void TryParseSteps_RejectsAnEmptyArray()
    {
        AutopilotPlanTools.TryParseSteps("[]", out var steps, out var error).Should().BeFalse();
        steps.Should().BeEmpty();
        error.Should().Contain("at least one step");
    }

    [Fact]
    public void TryParseSteps_RejectsInvalidJson()
    {
        AutopilotPlanTools.TryParseSteps("not json", out _, out var error).Should().BeFalse();
        error.Should().Contain("not valid JSON");
    }

    [Fact]
    public void TryParseSteps_RejectsAStepWithoutIdOrTitle()
    {
        AutopilotPlanTools.TryParseSteps("""[{"title":"no id","profile":"Claude"}]""", out _, out var error).Should().BeFalse();
        error.Should().Contain("id and a title");
    }

    // AC-210: the (profile, model) validity check the CEO's plan is held to.
    private static readonly IReadOnlyList<PluginProfileInfo> Roster =
    [
        new PluginProfileInfo("Claude", "Plugin", string.Empty) { ModelSuggestions = ["opus", "sonnet", "haiku"] },
        new PluginProfileInfo("Qwen (local)", "Ollama", string.Empty) { RunsLocally = true },
    ];

    private static AutopilotStep _Step(string profile, string? model) =>
        new("1", "Code", "do it", profile, model, "brief", "compiles", GateMode.Hard);

    [Fact]
    public void ValidateStepProfiles_AcceptsAModelOnTheProfilesList_AndAnEmptyModelForALocalProfile()
    {
        AutopilotPlanTools.ValidateStepProfiles([_Step("Claude", "opus")], Roster).Should().BeNull();
        // Case-insensitive: the CEO may write "Opus" where the roster lists "opus".
        AutopilotPlanTools.ValidateStepProfiles([_Step("Claude", "Sonnet")], Roster).Should().BeNull();
        AutopilotPlanTools.ValidateStepProfiles([_Step("Qwen (local)", null)], Roster).Should().BeNull();
    }

    [Fact]
    public void ValidateStepProfiles_RejectsAModelTheProfileDoesNotOffer()
    {
        var error = AutopilotPlanTools.ValidateStepProfiles([_Step("Claude", "gpt-5")], Roster);
        error.Should().Contain("Claude").And.Contain("gpt-5").And.Contain("opus, sonnet, haiku");
    }

    [Fact]
    public void ValidateStepProfiles_RejectsAChoiceProfileWithNoModel()
    {
        var error = AutopilotPlanTools.ValidateStepProfiles([_Step("Claude", null)], Roster);
        error.Should().Contain("Claude").And.Contain("no model");
    }

    [Fact]
    public void ValidateStepProfiles_RejectsAModelOnALocalProfileThatPinsItsOwn()
    {
        var error = AutopilotPlanTools.ValidateStepProfiles([_Step("Qwen (local)", "qwen2.5-coder")], Roster);
        error.Should().Contain("Qwen (local)").And.Contain("leave 'model' empty");
    }

    [Fact]
    public void ValidateStepProfiles_RejectsAProfileThatIsNotConfigured()
    {
        var error = AutopilotPlanTools.ValidateStepProfiles([_Step("Codex", null)], Roster);
        error.Should().Contain("Codex").And.Contain("not one of the configured profiles");
    }

    [Fact]
    public void ValidateStepProfiles_WithNoRoster_ValidatesNothing()
    {
        // With no roster to check against (a host that supplies none) the plan-time gate is a no-op — the roster is the
        // only source of truth it can check, and rejecting every plan would be worse than deferring to the embed-time net.
        AutopilotPlanTools.ValidateStepProfiles([_Step("Anything", "whatever")], []).Should().BeNull();
    }

    [Fact]
    public async Task SetPlan_RejectsAPlanWhoseStepModelIsNotOnItsProfile()
    {
        var (tools, _) = _PlanningTools();

        var result = await tools.SetPlan(
            "Ship it",
            """[{"id":"1","title":"Code","profile":"Claude","model":"gpt-5","brief":"b","hard":true}]""");

        _Ok(result).Should().BeFalse();
        result.Should().Contain("gpt-5");
    }

    [Fact]
    public async Task SetPlan_AcceptsAValidPlan_AndUpdatesTheController()
    {
        var (tools, controller) = _PlanningTools();

        var result = await tools.SetPlan(
            "Ship it",
            """
            [
              {"id":"1","title":"Code","profile":"Claude","model":"sonnet","brief":"b","hard":false},
              {"id":"2","title":"Local pass","profile":"Qwen (local)","brief":"b","hard":false}
            ]
            """);

        _Ok(result).Should().BeTrue();
        controller.Plan!.Steps.Should().HaveCount(2);
    }

    private static (AutopilotPlanTools Tools, AutopilotPlanController Controller) _PlanningTools(
        AutopilotPlanSource? source = null, ITrackerProvider? tracker = null)
    {
        var host = Substitute.For<ICockpitHost>();
        host.GetProfilesAsync().Returns(Task.FromResult(Roster));
        host.CurrentMcpCallerPaneId.Returns("pane-1");
        host.TrackerProviders.Returns(tracker is null ? [] : new[] { tracker });

        var controller = new AutopilotPlanController();
        controller.BeginPlanning(AutopilotPlan.Empty(source, goal: "Ship it"));
        controller.BindSession("pane-1");

        return (new AutopilotPlanTools(host, controller, new AutopilotSettings(new FakeStorage())), controller);
    }

    private static bool _Ok(string result) =>
        JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean();

    // AC-411: the child-stage code-gate. A step whose issueId names a tracker child other than the run's own source
    // issue is checked against the tracker's own stage before the plan is accepted.
    [Fact]
    public async Task SetPlan_RejectsAChildStep_WhoseTrackerStageIsNotExecutable()
    {
        var tracker = Substitute.For<ITrackerProvider>();
        tracker.TrackerId.Returns("youtrack");
        tracker.GetIssueSnapshotAsync("AC-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TrackerIssueSnapshot("Fix the widget", "Backlog")));
        var (tools, _) = _PlanningTools(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), tracker);

        var result = await tools.SetPlan(
            "Work the epic",
            """[{"id":"1","title":"Fix the child","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"}]""");

        _Ok(result).Should().BeFalse();
        result.Should().Contain("AC-1").And.Contain("Backlog");
    }

    [Fact]
    public async Task SetPlan_RejectsAChildStep_StillMarkedBrainstorm_OnTheIssuesOwnTitle()
    {
        // The tracker's own title carries the marker, not the CEO's step title — proving the check reads the real
        // issue rather than trusting whatever the CEO wrote into the plan (the exact gap AC-345's brief-only version left).
        var tracker = Substitute.For<ITrackerProvider>();
        tracker.TrackerId.Returns("youtrack");
        tracker.GetIssueSnapshotAsync("AC-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TrackerIssueSnapshot("[Brainstorm] a loose idea", "Ready")));
        var (tools, _) = _PlanningTools(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), tracker);

        var result = await tools.SetPlan(
            "Work the epic",
            """[{"id":"1","title":"A perfectly normal-sounding step title","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"}]""");

        _Ok(result).Should().BeFalse();
        result.Should().Contain("Brainstorm");
    }

    [Fact]
    public async Task SetPlan_AcceptsAChildStep_OnTheExecutableStage()
    {
        var tracker = Substitute.For<ITrackerProvider>();
        tracker.TrackerId.Returns("youtrack");
        tracker.GetIssueSnapshotAsync("AC-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TrackerIssueSnapshot("Fix the widget", "Ready")));
        var (tools, controller) = _PlanningTools(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), tracker);

        var result = await tools.SetPlan(
            "Work the epic",
            """[{"id":"1","title":"Fix the child","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"}]""");

        _Ok(result).Should().BeTrue();
        controller.Plan!.Steps[0].SourceIssueId.Should().Be("AC-1");
    }

    [Fact]
    public async Task SetPlan_DoesNotRecheckTheRunsOwnSourceIssue()
    {
        // The item the operator clicked already passed the gate before this planning round opened (AC-345) — a step
        // that names that same issue is not re-fetched from the tracker.
        var tracker = Substitute.For<ITrackerProvider>();
        tracker.TrackerId.Returns("youtrack");
        var (tools, _) = _PlanningTools(new AutopilotPlanSource("youtrack", "AC-343", "A single item"), tracker);

        var result = await tools.SetPlan(
            "Ship it",
            """[{"id":"1","title":"Do the work","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-343"}]""");

        _Ok(result).Should().BeTrue();
        await tracker.DidNotReceive().GetIssueSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPlan_WithTwoStepsOnTheSameChild_FetchesItsSnapshotOnlyOnce()
    {
        var tracker = Substitute.For<ITrackerProvider>();
        tracker.TrackerId.Returns("youtrack");
        tracker.GetIssueSnapshotAsync("AC-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TrackerIssueSnapshot("Fix the widget", "Ready")));
        var (tools, _) = _PlanningTools(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), tracker);

        var result = await tools.SetPlan(
            "Work the epic",
            """
            [
              {"id":"1","title":"Part one","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"},
              {"id":"2","title":"Part two","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"}
            ]
            """);

        _Ok(result).Should().BeTrue();
        await tracker.Received(1).GetIssueSnapshotAsync("AC-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPlan_WithNoIssueIdOnAStep_SkipsTheChildGate()
    {
        var tracker = Substitute.For<ITrackerProvider>();
        tracker.TrackerId.Returns("youtrack");
        var (tools, _) = _PlanningTools(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), tracker);

        var result = await tools.SetPlan(
            "Work the epic",
            """[{"id":"1","title":"A step with no tracker item","profile":"Claude","model":"sonnet","brief":"b","hard":false}]""");

        _Ok(result).Should().BeTrue();
    }

    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }
}
