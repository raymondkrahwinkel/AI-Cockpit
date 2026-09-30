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

    // AC-210: the (profile, model) validity check the CEO's plan is held to. AC-1342: Claude also declares effort
    // levels, Qwen (local) declares none — the two shapes ValidateStepEffort has to tell apart.
    private static readonly IReadOnlyList<PluginProfileInfo> Roster =
    [
        new PluginProfileInfo("Claude", "Plugin", string.Empty) { ModelSuggestions = ["opus", "sonnet", "haiku"], EffortSuggestions = ["low", "medium", "high"] },
        new PluginProfileInfo("Qwen (local)", "Ollama", string.Empty) { RunsLocally = true },
    ];

    private static AutopilotStep _Step(string profile, string? model) =>
        new("1", "Code", "do it", profile, model, "brief", "compiles", GateMode.Hard);

    private static AutopilotStep _StepWithEffort(string profile, string? model, string? effort) =>
        _Step(profile, model) with { Effort = effort };

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

    // AC-1342: a known level is accepted, an unknown one refused and named; a profile with no declared levels
    // (Qwen (local)) validates nothing. Ordinal, not case-insensitive — "High" is refused even though "high" is
    // offered, matching how the value later lands in the launch options and how the driver itself compares it.
    [Theory]
    [InlineData("Claude", "sonnet", "high", true)]
    [InlineData("Claude", "sonnet", "hyperspeed", false)]
    [InlineData("Claude", "sonnet", "High", false)]
    [InlineData("Qwen (local)", null, "whatever", true)]
    public void ValidateStepProfiles_ChecksEffortAgainstTheProfilesDeclaredLevels(string profile, string? model, string effort, bool accepted)
    {
        var error = AutopilotPlanTools.ValidateStepProfiles([_StepWithEffort(profile, model, effort)], Roster);

        Assert.Equal(accepted, error is null);
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

        return (new AutopilotPlanTools(host, controller, new AutopilotSettings(new FakeStorage()), _Manager()), controller);
    }

    // A manager backed by its own in-memory storage, with no runner wired — Submit just enqueues, so the
    // auto-submit test below can assert on the queue without a live session or a running pump.
    private static AutopilotRunManager _Manager() =>
        new(new AutopilotRunQueue(new FakeStorage()), new AutopilotSettings(new FakeStorage()));

    // AC-1339: grooming is the approval — a ticket acceptance section needs no operator click once the CEO's
    // plan carries both review gates; missing either one leaves the plan waiting for "Approve plan & start" as
    // always. Steps JSON varies too, so a plan with only one review gate is exercised, not just the acceptance flag.
    private const string BothGatesStepsJson = """
        [
          {"id":"1","title":"Build it","profile":"Claude","model":"sonnet","brief":"b","hard":true},
          {"id":"2","title":"Code review","profile":"Claude","model":"sonnet","brief":"b","reviewGate":true},
          {"id":"3","title":"Security review","profile":"Claude","model":"sonnet","brief":"b","reviewGate":true}
        ]
        """;

    private const string OneGateStepsJson = """
        [
          {"id":"1","title":"Build it","profile":"Claude","model":"sonnet","brief":"b","hard":true},
          {"id":"2","title":"Code review","profile":"Claude","model":"sonnet","brief":"b","reviewGate":true}
        ]
        """;

    public static IEnumerable<object[]> GroomedSubmissions() =>
    [
        ["Meets the bar when X and Y hold.", BothGatesStepsJson, 1],
        ["", BothGatesStepsJson, 0],
        ["Meets the bar when X and Y hold.", OneGateStepsJson, 0],
    ];

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

    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }
}
