using System.Text.Json;

namespace Cockpit.Plugin.YouTrack.Tests;

// The flows this plugin ships (#69). They are written as text rather than built with the workflows plugin's model —
// the two plugins cannot see each other — so nothing but a test stands between a typo in an id and a template that
// opens as a canvas of steps wired to nothing.
public class YouTrackWorkflowTemplateTests
{
    [Fact]
    public void EveryTemplate_IsAFlowThatCanBeRead()
    {
        foreach (var template in YouTrackWorkflowTemplates.All)
        {
            var flow = JsonDocument.Parse(template.Json).RootElement;

            Assert.False(string.IsNullOrWhiteSpace(flow.GetProperty("Name").GetString()));
            Assert.True(flow.GetProperty("Nodes").GetArrayLength() > 0, "a template with no steps is a blank canvas with a name");
            Assert.False(flow.GetProperty("IsActive").GetBoolean(), "a flow nobody has read yet must not already be armed");
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
                Assert.Contains(wire.GetProperty("FromNodeId").GetString(), ids);
                Assert.Contains(wire.GetProperty("ToNodeId").GetString(), ids);
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
        var contributed = YouTrackWorkflowSteps.All(new YouTrackSettings(new EmptyStorage()))
            .Select(step => step.TypeId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var template in YouTrackWorkflowTemplates.All)
        {
            var types = JsonDocument.Parse(template.Json).RootElement
                .GetProperty("Nodes")
                .EnumerateArray()
                .Select(node => node.GetProperty("TypeId").GetString() ?? string.Empty);

            foreach (var typeId in types)
            {
                var resolvable = typeId.StartsWith("cockpit.", StringComparison.Ordinal) || contributed.Contains(typeId);

                Assert.True(resolvable, $"'{template.Id}' uses '{typeId}', which is neither a cockpit step nor one this plugin contributes");
            }
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
