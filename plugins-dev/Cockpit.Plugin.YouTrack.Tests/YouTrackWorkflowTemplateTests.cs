using System.Text.Json;
using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The flows this plugin ships (#69). They are written as text rather than built with the workflows plugin's model —
/// the two plugins cannot see each other — so nothing but a test stands between a typo in an id and a template that
/// opens as a canvas of steps wired to nothing.
/// </summary>
public class YouTrackWorkflowTemplateTests
{
    [Fact]
    public void EveryTemplate_IsAFlowThatCanBeRead()
    {
        foreach (var template in YouTrackWorkflowTemplates.All)
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
        foreach (var template in YouTrackWorkflowTemplates.All)
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

    // A template built on a step this plugin does not contribute is one the operator opens to find a node the editor
    // cannot resolve.
    [Fact]
    public void EveryYouTrackStepATemplateUses_IsOneThisPluginContributes()
    {
        var contributed = YouTrackWorkflowSteps.All(new YouTrackSettings(new EmptyStorage()))
            .Select(step => step.TypeId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var template in YouTrackWorkflowTemplates.All)
        {
            var types = JsonDocument.Parse(template.Json).RootElement
                .GetProperty("Nodes")
                .EnumerateArray()
                .Select(node => node.GetProperty("TypeId").GetString() ?? string.Empty)
                .Where(typeId => typeId.StartsWith("youtrack.", StringComparison.Ordinal));

            types.Should().OnlyContain(typeId => contributed.Contains(typeId));
        }
    }

    private sealed class EmptyStorage : Cockpit.Plugins.Abstractions.IPluginStorage
    {
        public T? Get<T>(string key) => default;

        public void Set<T>(string key, T value)
        {
        }
    }
}
