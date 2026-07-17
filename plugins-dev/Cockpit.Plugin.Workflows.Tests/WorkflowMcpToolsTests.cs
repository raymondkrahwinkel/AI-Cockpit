using System.Text.Json;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The workflow MCP tools (#AC-12): an agent can create a flow from steps + connections, see it listed, read it
/// back, arm it and delete it — the data path that does not need a running engine. Every typeId is validated
/// against the node catalog, and connections reference steps by index.
/// </summary>
public class WorkflowMcpToolsTests
{
    [Fact]
    public void Create_ThenList_Describe_SetActive_Delete_RoundTrips()
    {
        var storage = new _InMemoryStorage();
        var tools = new WorkflowMcpTools(new WorkflowStore(storage), new RunStore(storage), Substitute.For<ICockpitHost>());

        // Create a two-step flow: a manual trigger wired to a command.
        var created = _Json(tools.CreateWorkflow(
            "Build it",
            steps_json: """[{"typeId":"cockpit.manual","name":"Start"},{"typeId":"cockpit.command","name":"Build","parameters":{"Command":"echo hi"}}]""",
            connections_json: """[{"from":0,"output":0,"to":1}]"""));
        created.GetProperty("ok").GetBoolean().Should().BeTrue();
        var id = created.GetProperty("id").GetString()!;

        // It is listed, disarmed by default.
        var listed = _Json(tools.ListWorkflows());
        listed.EnumerateArray().Should().ContainSingle(flow => flow.GetProperty("id").GetString() == id)
            .Which.GetProperty("active").GetBoolean().Should().BeFalse();

        // It reads back with both steps and the connection between them.
        var described = _Json(tools.DescribeWorkflow(id));
        described.GetProperty("steps").GetArrayLength().Should().Be(2);
        described.GetProperty("steps")[1].GetProperty("parameters").GetProperty("Command").GetString().Should().Be("echo hi");
        var connection = described.GetProperty("connections")[0];
        connection.GetProperty("from").GetInt32().Should().Be(0);
        connection.GetProperty("to").GetInt32().Should().Be(1);

        // It can be armed, and deleted.
        _Json(tools.SetWorkflowActive(id, true)).GetProperty("active").GetBoolean().Should().BeTrue();
        _Json(tools.DeleteWorkflow(id)).GetProperty("ok").GetBoolean().Should().BeTrue();
        _Json(tools.ListWorkflows()).EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public void Create_WithAnUnknownStepType_IsRefused_NamingTheOffendingType()
    {
        var storage = new _InMemoryStorage();
        var tools = new WorkflowMcpTools(new WorkflowStore(storage), new RunStore(storage), Substitute.For<ICockpitHost>());

        var result = _Json(tools.CreateWorkflow("Bad", steps_json: """[{"typeId":"cockpit.not-a-real-step"}]""", connections_json: null));

        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Contain("cockpit.not-a-real-step");
    }

    private static JsonElement _Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private sealed class _InMemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<T>(value) : default;

        public void Set<T>(string key, T value) => _values[key] = JsonSerializer.Serialize(value);
    }
}
