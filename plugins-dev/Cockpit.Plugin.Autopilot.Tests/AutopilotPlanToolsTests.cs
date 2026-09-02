using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Tracking;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// The CEO plan-emit tool's parsing (AC-174): a well-formed steps array builds the plan; a malformed or half-formed one
// is turned down with a clear error rather than producing an unrunnable plan. The pane-scoping half is covered where
// the tool is wired (it uses the same CurrentMcpCallerPaneId gate as AutopilotMcpTools).
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

        Assert.True(AutopilotPlanTools.TryParseSteps(json, out var steps, out var error));
        Assert.Null(error);
        Assert.Equal(2, System.Linq.Enumerable.Count(steps));
        Assert.Equivalent(new
        {
            Id = "1", Title = "Code", ProfileLabel = "Claude", Model = "Sonnet", Mode = GateMode.Skip, Status = AutopilotStepStatus.Pending,
        }, steps[0]);
        Assert.Equal(GateMode.Hard, steps[1].Mode);
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

        Assert.True(AutopilotPlanTools.TryParseSteps(json, out var steps, out _));
        Assert.True(steps[0].IsReviewGate);
        Assert.Equal(GateMode.Hard, steps[0].Mode);
        Assert.False(steps[1].IsReviewGate);
    }

    [Fact]
    public void TryParseSteps_OptionalFields_AreFilledIn_OmittedOrMalformed()
    {
        // What a step gets when the CEO leaves an optional field out or writes nonsense into it. One array, because
        // these only mean anything together: the same parse that keeps a named MCP server has to drop the blank next
        // to it, leave the bare step with nothing, and clamp an agent count that would launch no agent at all.
        const string json = """
            [
              {"id":"1","title":"Visual verify","profile":"Claude","brief":"b","model":"Sonnet","mcp":["cockpit-verify","  "],"agents":3},
              {"id":"2","title":"Conventions","profile":"Qwen (local)","brief":"b"},
              {"id":"3","title":"PR","profile":"Claude","brief":"b","agents":0}
            ]
            """;

        Assert.True(AutopilotPlanTools.TryParseSteps(json, out var steps, out _));

        Assert.Equal(new[] { "cockpit-verify" }, steps[0].McpServers);
        Assert.Equal(3, steps[0].AgentCount);
        // A local profile pins its own model, so an omitted one reads back as null rather than blank.
        Assert.Null(steps[1].Model);
        Assert.Empty(steps[1].McpServers);
        Assert.Equal(1, steps[1].AgentCount);
        Assert.Equal(1, steps[2].AgentCount);
    }

    public static IEnumerable<object[]> MalformedPlans() =>
    [
        ["[]", "at least one step"],
        ["not json", "not valid JSON"],
        ["""[{"title":"no id","profile":"Claude"}]""", "id and a title"],
    ];

    [Theory]
    [MemberData(nameof(MalformedPlans))]
    public void TryParseSteps_RefusesAPlanItCannotRun_AndSaysWhy(string json, string expectedReason)
    {
        Assert.False(AutopilotPlanTools.TryParseSteps(json, out var steps, out var error));
        Assert.Empty(steps);
        Assert.Contains(expectedReason, error);
    }

    // AC-210: the (profile, model) validity check the CEO's plan is held to.
    private static readonly IReadOnlyList<PluginProfileInfo> Roster =
    [
        new PluginProfileInfo("Claude", "Plugin", string.Empty) { ModelSuggestions = ["opus", "sonnet", "haiku"] },
        new PluginProfileInfo("Qwen (local)", "Ollama", string.Empty) { RunsLocally = true },
    ];

    private static AutopilotStep _Step(string profile, string? model) =>
        new("1", "Code", "do it", profile, model, "brief", "compiles", GateMode.Hard);

    [Theory]
    [InlineData("Claude", "opus")]
    // Case-insensitive: the CEO may write "Sonnet" where the roster lists "sonnet".
    [InlineData("Claude", "Sonnet")]
    // A local profile pins its own model, so it is the one case where an empty model is right.
    [InlineData("Qwen (local)", null)]
    public void ValidateStepProfiles_AcceptsAPairingTheRosterOffers(string profile, string? model) =>
        Assert.Null(AutopilotPlanTools.ValidateStepProfiles([_Step(profile, model)], Roster));

    public static IEnumerable<object[]> UnofferedPairings() =>
    [
        // A model the profile does not offer is named alongside the ones it does, so the CEO can redraft in one pass.
        ["Claude", "gpt-5", new[] { "Claude", "gpt-5", "opus, sonnet, haiku" }],
        ["Claude", null!, new[] { "Claude", "no model" }],
        ["Qwen (local)", "qwen2.5-coder", new[] { "Qwen (local)", "leave 'model' empty" }],
        ["Codex", null!, new[] { "Codex", "not one of the configured profiles" }],
    ];

    [Theory]
    [MemberData(nameof(UnofferedPairings))]
    public void ValidateStepProfiles_RejectsAndNamesTheOffendingProfile(string profile, string? model, string[] expected)
    {
        var error = AutopilotPlanTools.ValidateStepProfiles([_Step(profile, model)], Roster);

        Assert.All(expected, fragment => Assert.Contains(fragment, error));
    }

    [Fact]
    public void ValidateStepProfiles_WithNoRoster_ValidatesNothing()
    {
        // With no roster to check against (a host that supplies none) the plan-time gate is a no-op — the roster is the
        // only source of truth it can check, and rejecting every plan would be worse than deferring to the embed-time net.
        Assert.Null(AutopilotPlanTools.ValidateStepProfiles([_Step("Anything", "whatever")], []));
    }

    [Fact]
    public async Task SetPlan_RejectsAPlanWhoseStepModelIsNotOnItsProfile()
    {
        var (tools, _) = _PlanningTools();

        var result = await tools.SetPlan(
            "Ship it",
            """[{"id":"1","title":"Code","profile":"Claude","model":"gpt-5","brief":"b","hard":true}]""");

        Assert.False(_Ok(result));
        Assert.Contains("gpt-5", result);
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

        Assert.True(_Ok(result));
        Assert.Equal(2, System.Linq.Enumerable.Count(controller.Plan!.Steps));
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

    private static ITrackerProvider _TrackerWith(string childTitle, string childStage)
    {
        var tracker = Substitute.For<ITrackerProvider>();
        tracker.TrackerId.Returns("youtrack");
        tracker.GetIssueSnapshotAsync("AC-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TrackerIssueSnapshot(childTitle, childStage)));
        return tracker;
    }

    private static bool _Ok(string result) =>
        JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean();

    // AC-411: the child-stage code-gate. A step whose issueId names a tracker child other than the run's own source
    // issue is checked against the tracker's own snapshot before the plan is accepted — and the snapshot is what
    // decides, not the CEO's step title, which is the exact gap AC-345's brief-only version left.
    public static IEnumerable<object[]> RefusedChildren() =>
    [
        ["Fix the widget", "Backlog", new[] { "AC-1", "Backlog" }],
        ["[Brainstorm] a loose idea", "Ready", new[] { "Brainstorm" }],
    ];

    [Theory]
    [MemberData(nameof(RefusedChildren))]
    public async Task SetPlan_RejectsAChildStep_TheTrackersOwnSnapshotRulesOut(string childTitle, string childStage, string[] expected)
    {
        var (tools, _) = _PlanningTools(
            new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"),
            _TrackerWith(childTitle, childStage));

        var result = await tools.SetPlan(
            "Work the epic",
            """[{"id":"1","title":"A perfectly normal-sounding step title","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"}]""");

        Assert.False(_Ok(result));
        Assert.All(expected, fragment => Assert.Contains(fragment, result));
    }

    [Fact]
    public async Task SetPlan_AcceptsAChildStep_OnTheExecutableStage()
    {
        var (tools, controller) = _PlanningTools(
            new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"),
            _TrackerWith("Fix the widget", "Ready"));

        var result = await tools.SetPlan(
            "Work the epic",
            """[{"id":"1","title":"Fix the child","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"}]""");

        Assert.True(_Ok(result));
        Assert.Equal("AC-1", controller.Plan!.Steps[0].SourceIssueId);
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

        Assert.True(_Ok(result));
        await tracker.DidNotReceive().GetIssueSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPlan_WithTwoStepsOnTheSameChild_FetchesItsSnapshotOnlyOnce()
    {
        var tracker = _TrackerWith("Fix the widget", "Ready");
        var (tools, _) = _PlanningTools(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), tracker);

        var result = await tools.SetPlan(
            "Work the epic",
            """
            [
              {"id":"1","title":"Part one","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"},
              {"id":"2","title":"Part two","profile":"Claude","model":"sonnet","brief":"b","hard":false,"issueId":"AC-1"}
            ]
            """);

        Assert.True(_Ok(result));
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

        Assert.True(_Ok(result));
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
