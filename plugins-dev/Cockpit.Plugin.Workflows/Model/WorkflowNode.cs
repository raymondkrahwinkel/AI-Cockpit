using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// One step on the canvas (#69): an instance of a <see cref="NodeTypeDescriptor"/>, with its own name, its place
/// and its parameter values. What it <em>does</em> lives in the type, not here — which is what lets a plugin one
/// day contribute a type this editor has never heard of and have it draw and run like any other.
/// </summary>
public sealed class WorkflowNode
{
    public required string Id { get; init; }

    /// <summary>The type this is an instance of ("cockpit.notify").</summary>
    public required string TypeId { get; init; }

    /// <summary>What the operator called it. Three "Notify" nodes in one flow are otherwise indistinguishable.</summary>
    public required string Name { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>The values for this type's parameters, by name. Opaque here on purpose: the editor never needs to know what a node type stores.</summary>
    public Dictionary<string, string> Parameters { get; init; } = [];

    /// <summary>A node the operator switched off: it stays on the canvas, drawn dimmed, and a run skips it. Deleting is not the only way to say "not now".</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Print what this step hands on into the run log, in full. The debug switch: what a step produced is the thing you need when a flow does the wrong thing, and a one-line summary is not it.</summary>
    public bool IsTraced { get; set; }

    /// <summary>
    /// Carry on down the ordinary wire when this step fails, instead of stopping the branch. For the steps whose
    /// failure is not the point — a notification nobody received should not stop a deploy that worked.
    /// <para>
    /// An <em>error wire</em> (see <see cref="ErrorOutput"/>) says more and says it better: it sends the failure
    /// somewhere. This is the blunt version, for when there is nowhere to send it.
    /// </para>
    /// </summary>
    public bool ContinueOnError { get; set; }

    /// <summary>
    /// Whether this step shows a way out for failure. Off by default: most steps in most flows have no error path, and
    /// a red pin on every one of them is a canvas full of a decision nobody made. Switch it on for the steps whose
    /// failure you actually want to handle, and the red pin appears to wire.
    /// <para>
    /// A step that already has a wire from its error pin keeps showing it whatever this says — the flow says louder
    /// than the checkbox that somebody meant it.
    /// </para>
    /// </summary>
    public bool HasErrorPath { get; set; }

    /// <summary>
    /// The way out a step takes when it fails — one past its ordinary ones, so it never collides with a decision's
    /// branches. Wire it and a failure has somewhere to go: tell Slack, move the ticket back, try something else.
    /// Unwired, a failure stops that branch, which is what it should do when nobody said otherwise.
    /// </summary>
    [JsonIgnore]
    public int ErrorOutput => Outputs.Count;

    /// <summary>
    /// The type, or null when the flow refers to a type this build does not have (an uninstalled plugin's node) — which
    /// is shown as such rather than crashing the canvas.
    /// <para>
    /// Never written to the file: it is looked up from <see cref="TypeId"/>, so storing it would be storing the same
    /// thing twice — and a stale copy of it at that. It cannot be written either, since a type can carry a function
    /// (what a field's values are), and saving a flow with one in it threw.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public NodeTypeDescriptor? Type => NodeCatalog.Find(TypeId);

    [JsonIgnore]
    public WorkflowNodeKind Kind => Type?.Kind ?? WorkflowNodeKind.Action;

    [JsonIgnore]
    public bool HasInput => Type?.HasInput ?? true;

    /// <summary>
    /// What this step's ways out are called. Fixed by the type for every node but one: a switch's pins are the cases
    /// the operator wrote into it (see <see cref="SwitchCases"/>), so they are read from this node's own parameters.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Outputs => TypeId == SwitchCases.TypeId
        ? SwitchCases.Outputs(Parameters)
        : Type?.Outputs ?? [""];
}
