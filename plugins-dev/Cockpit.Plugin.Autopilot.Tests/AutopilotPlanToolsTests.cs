using FluentAssertions;

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
}
