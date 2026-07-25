using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Profiles;
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

    private static (AutopilotPlanTools Tools, AutopilotPlanController Controller) _PlanningTools()
    {
        var host = Substitute.For<ICockpitHost>();
        host.GetProfilesAsync().Returns(Task.FromResult(Roster));
        host.CurrentMcpCallerPaneId.Returns("pane-1");

        var controller = new AutopilotPlanController();
        controller.BeginPlanning(AutopilotPlan.Empty(source: null, goal: "Ship it"));
        controller.BindSession("pane-1");

        return (new AutopilotPlanTools(host, controller), controller);
    }

    private static bool _Ok(string result) =>
        JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean();
}
