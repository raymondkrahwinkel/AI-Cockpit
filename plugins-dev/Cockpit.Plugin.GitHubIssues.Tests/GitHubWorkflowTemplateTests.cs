using System.Text.Json;
using FluentAssertions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The flow this plugin ships (#69) is written as text, not built with the workflows plugin's model — the two plugins
/// cannot see each other. Nothing compiles the ids inside it, so a typo travels through the build, through CI and into
/// the store, and surfaces as a canvas of steps wired to nothing the first time an operator opens the template.
/// </summary>
public class GitHubWorkflowTemplateTests
{
    [Fact]
    public void EveryTemplate_IsAFlowThatCanBeRead()
    {
        foreach (var template in GitHubWorkflowTemplates.All)
        {
            var flow = JsonDocument.Parse(template.Json).RootElement;

            flow.GetProperty("Name").GetString().Should().NotBeNullOrWhiteSpace();
            flow.GetProperty("Nodes").GetArrayLength().Should().BeGreaterThan(0, "a template with no steps is a blank canvas with a name");
            flow.GetProperty("IsActive").GetBoolean().Should().BeFalse("a flow nobody has read yet must not already be armed");
        }
    }

    // The wires are stored by step id. A wire to an id that is not in the flow is a step that never runs, and the
    // canvas shows no reason why.
    [Fact]
    public void EveryWire_RunsBetweenStepsThatAreInTheFlow()
    {
        foreach (var template in GitHubWorkflowTemplates.All)
        {
            var flow = JsonDocument.Parse(template.Json).RootElement;
            var ids = flow.GetProperty("Nodes")
                .EnumerateArray()
                .Select(node => node.GetProperty("Id").GetString())
                .ToHashSet(StringComparer.Ordinal);

            foreach (var wire in flow.GetProperty("Connections").EnumerateArray())
            {
                ids.Should().Contain(wire.GetProperty("FromNodeId").GetString(), $"'{template.Id}' wires from a step it does not have");
                ids.Should().Contain(wire.GetProperty("ToNodeId").GetString(), $"'{template.Id}' wires to a step it does not have");
            }
        }
    }

    // A template that ships with a plugin has to stand on what is certainly there: the cockpit's own steps, plus what
    // this plugin contributes. The editor resolves steps from one flat list across every installed plugin, so borrowing
    // another plugin's step would work — right until the operator has not installed it. Every id is checked rather than
    // only the ones already carrying this plugin's prefix, because a typo in the prefix is one of the ways to get here
    // and filtering on it first would drop exactly that case.
    [Fact]
    public void EveryStepATemplateUses_IsACockpitStepOrOneThisPluginContributes()
    {
        var contributed = GitHubWorkflowSteps.All(new GitHubIssuesSettings(new InMemoryPluginStorage()))
            .Select(step => step.TypeId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var template in GitHubWorkflowTemplates.All)
        {
            var types = JsonDocument.Parse(template.Json).RootElement
                .GetProperty("Nodes")
                .EnumerateArray()
                .Select(node => node.GetProperty("TypeId").GetString() ?? string.Empty);

            foreach (var typeId in types)
            {
                var resolvable = typeId.StartsWith("cockpit.", StringComparison.Ordinal) || contributed.Contains(typeId);

                resolvable.Should().BeTrue($"'{template.Id}' uses '{typeId}', which is neither a cockpit step nor one this plugin contributes");
            }
        }
    }
}
