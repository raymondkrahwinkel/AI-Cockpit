using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Plugin.Workflows.Model;
using Material.Icons;

namespace Cockpit.Plugin.Workflows.Contracts;

// AC-1399: what the backend part answers the UI part over the plugin's channel. Compiled into both assemblies as a
// linked source file rather than shared as an assembly, so the UI part never references the backend part. A flow
// crosses as the text WorkflowJson writes, the same text the store keeps and an export shares.
internal static class WorkflowsChannel
{
    // Payload: none. Answers the stored flows as WorkflowJson.WriteAll text.
    public const string Load = "load";

    // Payload: WorkflowsSaveRequest. Replaces the stored flows; answers null.
    public const string Save = "save";

    // Payload: none. Answers a WorkflowsStepCatalog — the steps other plugins contributed, read at the moment of asking.
    public const string Catalog = "catalog";

    // Payload: WorkflowsSuggestRequest. Answers the contributed step's suggestions for one of its parameters.
    public const string Suggest = "suggest";

    // Payload: none. Answers the WorkflowTemplate list the installed plugins registered.
    public const string Templates = "templates";

    // Payload: WorkflowsLastRunRequest. Answers the flow's most recent WorkflowRun, or null.
    public const string LastRun = "last-run";

    // Payload: WorkflowsRunRequest. Runs the flow as the operator from that trigger; answers the WorkflowRun.
    public const string Run = "run";

    // Event, payload WorkflowRun: a run of any origin (the editor, a trigger, an agent) was recorded.
    public const string RunRecorded = "run-recorded";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

internal sealed record WorkflowsSaveRequest(string Json);

internal sealed record WorkflowsSuggestRequest(string TypeId, string Parameter);

internal sealed record WorkflowsLastRunRequest(string WorkflowId);

internal sealed record WorkflowsRunRequest(string WorkflowJson, string TriggerNodeId);

// Undeclared names the steps left out because they do not declare their consent (#AC-38), so the UI can say so.
internal sealed record WorkflowsStepCatalog(IReadOnlyList<WorkflowsStepType> Contributed, IReadOnlyList<string> Undeclared);

// A contributed NodeTypeDescriptor without its Suggest callback, which cannot cross; the UI part puts one back that
// asks the backend (WorkflowsChannel.Suggest).
internal sealed record WorkflowsStepType(
    string Id,
    string Name,
    string Description,
    string Icon,
    NodeCategory Category,
    WorkflowNodeKind Kind,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Parameters,
    IReadOnlyDictionary<string, string>? Sample,
    string? Group,
    MaterialIconKind? IconKind)
{
    public static WorkflowsStepType Of(NodeTypeDescriptor type) => new(
        type.Id, type.Name, type.Description, type.Icon, type.Category, type.Kind, type.Outputs, type.Parameters,
        type.Sample, type.Group, type.IconKind);

    public NodeTypeDescriptor Describe(Func<string, CancellationToken, Task<IReadOnlyList<string>>> suggest) =>
        new(Id, Name, Description, Icon, Category, Kind, Outputs, Parameters, Sample, Group, suggest)
        {
            IconKind = IconKind,
        };
}
