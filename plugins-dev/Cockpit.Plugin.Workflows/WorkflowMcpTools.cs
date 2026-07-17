using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// The MCP tools an agent uses to work with cockpit workflows (#AC-12), exposed as <c>mcp__cockpit-workflows__*</c>
/// through the plugin's own MCP server (contributed via <see cref="ICockpitHost.AddMcpEndpoint"/>). An agent can see
/// which flows exist, read one, learn the step types it can build from, run a flow, and create/edit/arm/delete one —
/// so a flow the operator drew, or one the agent assembled, is a tool the agent can reach for when it fits the work.
/// </summary>
/// <remarks>
/// A workflow is plain data (nodes with a TypeId + string parameters, and index-referenced connections), so the
/// create/update tools take that shape and this class turns it into a saved <see cref="Workflow"/>. The engine is
/// built per run (never cached) because contributed steps register in an uncontrolled order — the same reason the
/// watcher builds it lazily.
/// </remarks>
internal sealed class WorkflowMcpTools
{
    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = false };

    private readonly WorkflowStore _store;
    private readonly RunStore _runs;
    private readonly ICockpitHost _host;

    public WorkflowMcpTools(WorkflowStore store, RunStore runs, ICockpitHost host)
    {
        _store = store;
        _runs = runs;
        _host = host;
    }

    [McpServerTool(Name = "list_workflows")]
    [Description("Lists the saved cockpit workflows — the flows the operator drew and armed — with their id, name, whether they are active (auto-firing), and how many steps each has. Use it to see what is available before running or editing one.")]
    public string ListWorkflows() =>
        JsonSerializer.Serialize(
            _store.Load().Select(flow => new
            {
                id = flow.Id,
                name = flow.Name,
                active = flow.IsActive,
                steps = flow.Nodes.Count,
                triggers = flow.Nodes.Where(node => node.Kind == WorkflowNodeKind.Trigger).Select(node => node.TypeId),
            }),
            Serializer);

    [McpServerTool(Name = "describe_workflow")]
    [Description("Returns a workflow's full structure: its steps (each with an index, id, type, name and parameters) and the connections between them (from-step index, output, to-step index). Read it before editing a flow, or to understand what one does.")]
    public string DescribeWorkflow(
        [Description("The workflow id, as returned by list_workflows.")] string id)
    {
        if (_store.Load().FirstOrDefault(flow => flow.Id == id) is not { } workflow)
        {
            return _Fail($"No workflow with id '{id}'.");
        }

        var index = workflow.Nodes.Select((node, i) => (node.Id, i)).ToDictionary(pair => pair.Id, pair => pair.i);
        return JsonSerializer.Serialize(
            new
            {
                id = workflow.Id,
                name = workflow.Name,
                active = workflow.IsActive,
                steps = workflow.Nodes.Select((node, i) => new
                {
                    index = i,
                    id = node.Id,
                    typeId = node.TypeId,
                    name = node.Name,
                    disabled = node.IsDisabled,
                    parameters = node.Parameters,
                }),
                connections = workflow.Connections.Select(connection => new
                {
                    from = index.GetValueOrDefault(connection.FromNodeId, -1),
                    output = connection.FromOutput,
                    to = index.GetValueOrDefault(connection.ToNodeId, -1),
                }),
            },
            Serializer);
    }

    [McpServerTool(Name = "list_workflow_step_types")]
    [Description("Lists the step types a workflow can be built from — triggers, actions and decisions — each with its typeId, name, description, kind, the parameter names it takes and its output labels. Use it before create_workflow or update_workflow so you name valid typeIds and fill the right parameters.")]
    public string ListStepTypes() =>
        JsonSerializer.Serialize(
            NodeCatalog.All.Select(type => new
            {
                typeId = type.Id,
                name = type.Name,
                description = type.Description,
                kind = type.Kind.ToString(),
                parameters = type.Parameters,
                outputs = type.Outputs,
            }),
            Serializer);

    [McpServerTool(Name = "run_workflow")]
    [Description("Runs a workflow now, from its manual-start (▶) step, and waits for it to finish. Returns the run's id, status (Completed/Failed/…) and, on failure, why. The flow needs a manual trigger step to be runnable this way; a purely event-triggered flow has nothing to start by hand. The run also appears in the cockpit's workflow run history.")]
    public async Task<string> RunWorkflow(
        [Description("The workflow id, as returned by list_workflows.")] string id)
    {
        if (_store.Load().FirstOrDefault(flow => flow.Id == id) is not { } workflow)
        {
            return _Fail($"No workflow with id '{id}'.");
        }

        // Prefer a manual trigger that is wired and enabled — the same choice the editor's Run button makes.
        var manual = workflow.Nodes
            .Where(node => node.TypeId == "cockpit.manual" && !node.IsDisabled)
            .OrderByDescending(node => workflow.Connections.Any(connection => connection.FromNodeId == node.Id))
            .FirstOrDefault();
        if (manual is null)
        {
            return _Fail("This workflow has no manual-start step to run by hand. Add a '▶ Start by hand' trigger, or trigger it by its event.");
        }

        var engine = EngineFactory.Create(_host, _host.WorkflowSteps);
        var run = await engine.RunAsync(workflow, manual.Id, RunOrigin.McpAgent);
        _runs.Add(run);

        return JsonSerializer.Serialize(
            new { ok = run.Status != RunStatus.Failed, runId = run.Id, status = run.Status.ToString(), error = run.Error },
            Serializer);
    }

    [McpServerTool(Name = "set_workflow_active")]
    [Description("Arms or disarms a workflow: an active flow fires on its own when its trigger's event happens; an inactive one only runs when you run it by hand. Use it to turn an event-triggered flow on or off.")]
    public string SetWorkflowActive(
        [Description("The workflow id.")] string id,
        [Description("True to arm the flow (let it auto-fire), false to disarm it.")] bool active)
    {
        var flows = _store.Load().ToList();
        if (flows.FirstOrDefault(flow => flow.Id == id) is not { } workflow)
        {
            return _Fail($"No workflow with id '{id}'.");
        }

        // Arming a flow that could auto-fire a dangerous step is the operator's to do; disarming is always allowed.
        if (active && _DangerousNode(workflow) is { } dangerous)
        {
            return _RefuseDangerous(dangerous);
        }

        workflow.IsActive = active;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        _store.Save(flows);
        return JsonSerializer.Serialize(new { ok = true, id, active }, Serializer);
    }

    [McpServerTool(Name = "delete_workflow")]
    [Description("Deletes a saved workflow. This cannot be undone from here.")]
    public string DeleteWorkflow(
        [Description("The workflow id.")] string id)
    {
        var flows = _store.Load().ToList();
        if (flows.RemoveAll(flow => flow.Id == id) == 0)
        {
            return _Fail($"No workflow with id '{id}'.");
        }

        _store.Save(flows);
        return JsonSerializer.Serialize(new { ok = true, id }, Serializer);
    }

    [McpServerTool(Name = "create_workflow")]
    [Description("Creates a new workflow from steps and connections. steps_json is a JSON array of { typeId, name?, parameters? } (typeId from list_workflow_step_types; parameters is an object keyed by that type's parameter names). connections_json is a JSON array of { from, output?, to } using step INDICES into the steps array (output defaults to 0; for a decision step 0/1 are its branches). Include a manual-start step ('cockpit.manual') if you want to run it by hand. The flow is created disarmed (not active) so it never fires until the operator or set_workflow_active turns it on. Returns the new workflow id.")]
    public string CreateWorkflow(
        [Description("A name for the workflow.")] string name,
        [Description("JSON array of steps: [{ \"typeId\": \"cockpit.command\", \"name\": \"Build\", \"parameters\": { \"Command\": \"dotnet build\" } }].")] string steps_json,
        [Description("JSON array of connections by step index: [{ \"from\": 0, \"output\": 0, \"to\": 1 }]. Omit or [] for none.")] string? connections_json = null)
    {
        try
        {
            var workflow = _BuildWorkflow(Guid.NewGuid().ToString("n"), name, steps_json, connections_json);
            if (_DangerousNode(workflow) is { } dangerous)
            {
                return _RefuseDangerous(dangerous);
            }

            var flows = _store.Load().ToList();
            flows.Add(workflow);
            _store.Save(flows);
            return JsonSerializer.Serialize(new { ok = true, id = workflow.Id, name = workflow.Name, steps = workflow.Nodes.Count }, Serializer);
        }
        catch (WorkflowSpecException ex)
        {
            return _Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "update_workflow")]
    [Description("Replaces an existing workflow's steps and connections (and optionally its name), keeping its id and armed state. Same steps_json/connections_json shape as create_workflow — send the whole flow as you want it, not a delta. Read it first with describe_workflow. Returns the workflow id.")]
    public string UpdateWorkflow(
        [Description("The workflow id to replace.")] string id,
        [Description("JSON array of steps, same shape as create_workflow.")] string steps_json,
        [Description("JSON array of connections by step index, same shape as create_workflow. Omit or [] for none.")] string? connections_json = null,
        [Description("A new name, or omit to keep the current one.")] string? name = null)
    {
        var flows = _store.Load().ToList();
        var existingIndex = flows.FindIndex(flow => flow.Id == id);
        if (existingIndex < 0)
        {
            return _Fail($"No workflow with id '{id}'.");
        }

        try
        {
            var replacement = _BuildWorkflow(id, name ?? flows[existingIndex].Name, steps_json, connections_json);
            if (_DangerousNode(replacement) is { } dangerous)
            {
                return _RefuseDangerous(dangerous);
            }

            replacement.IsActive = flows[existingIndex].IsActive;
            flows[existingIndex] = replacement;
            _store.Save(flows);
            return JsonSerializer.Serialize(new { ok = true, id, steps = replacement.Nodes.Count }, Serializer);
        }
        catch (WorkflowSpecException ex)
        {
            return _Fail(ex.Message);
        }
    }

    // The step types an MCP caller may not create or arm (#AC-38): they run with the operator's rights, so a flow
    // containing one is the operator's to build and arm in the editor. Derived from the engine's runners — one source
    // of truth with the runtime gate. Returns the first offending typeId, or null when the flow is clean.
    private string? _DangerousNode(Workflow workflow)
    {
        var gated = EngineFactory.Create(_host, _host.WorkflowSteps).ConsentRequiredTypeIds;
        return workflow.Nodes.FirstOrDefault(node => gated.Contains(node.TypeId))?.TypeId;
    }

    private string _RefuseDangerous(string typeId) => _Fail(
        $"'{typeId}' runs with your rights, so a workflow that contains it can only be created or armed by the operator in the editor — not over the MCP. Ask them to add or arm it. (An agent can still run a flow the operator built; it asks the operator to Approve each dangerous step.)");

    // Turns the agent's steps/connections spec into a saved-shape Workflow, validating every typeId and connection.
    private static Workflow _BuildWorkflow(string id, string name, string stepsJson, string? connectionsJson)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new WorkflowSpecException("A workflow needs a name.");
        }

        var steps = _ParseArray(stepsJson, "steps_json");
        if (steps.Count == 0)
        {
            throw new WorkflowSpecException("A workflow needs at least one step.");
        }

        var nodes = new List<WorkflowNode>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var typeId = _RequireString(step, "typeId", $"step {i}");
            if (NodeCatalog.Find(typeId) is null)
            {
                throw new WorkflowSpecException($"Step {i}: no step type '{typeId}'. Call list_workflow_step_types for the valid typeIds.");
            }

            var node = new WorkflowNode
            {
                Id = Guid.NewGuid().ToString("n"),
                TypeId = typeId,
                Name = step.TryGetProperty("name", out var stepName) && stepName.ValueKind == JsonValueKind.String && stepName.GetString() is { Length: > 0 } n
                    ? n
                    : NodeCatalog.Find(typeId)!.Name,
                // Canvas position is cosmetic; lay steps out in a readable line so an operator opening the flow sees it tidily.
                X = 80 + (i * 240),
                Y = 120,
            };

            if (step.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
            {
                foreach (var parameter in parameters.EnumerateObject())
                {
                    node.Parameters[parameter.Name] = parameter.Value.ValueKind == JsonValueKind.String
                        ? parameter.Value.GetString() ?? string.Empty
                        : parameter.Value.GetRawText();
                }
            }

            nodes.Add(node);
        }

        var connections = new List<WorkflowConnection>();
        foreach (var connection in _ParseArray(connectionsJson ?? "[]", "connections_json"))
        {
            var from = _RequireInt(connection, "from", "a connection");
            var to = _RequireInt(connection, "to", "a connection");
            var output = connection.TryGetProperty("output", out var outputValue) && outputValue.ValueKind == JsonValueKind.Number ? outputValue.GetInt32() : 0;
            if (from < 0 || from >= nodes.Count || to < 0 || to >= nodes.Count)
            {
                throw new WorkflowSpecException($"A connection refers to a step index outside 0..{nodes.Count - 1}.");
            }

            connections.Add(new WorkflowConnection { FromNodeId = nodes[from].Id, FromOutput = output, ToNodeId = nodes[to].Id });
        }

        return new Workflow { Id = id, Name = name.Trim(), IsActive = false, Nodes = nodes, Connections = connections };
    }

    private static IReadOnlyList<JsonElement> _ParseArray(string json, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new WorkflowSpecException($"{field} must be a JSON array.");
            }

            return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
        }
        catch (JsonException)
        {
            throw new WorkflowSpecException($"{field} is not valid JSON.");
        }
    }

    private static string _RequireString(JsonElement element, string property, string where) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new WorkflowSpecException($"{where} is missing a '{property}'.");

    private static int _RequireInt(JsonElement element, string property, string where) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : throw new WorkflowSpecException($"{where} is missing a numeric '{property}'.");

    private static string _Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Serializer);

    private sealed class WorkflowSpecException(string message) : Exception(message);
}
