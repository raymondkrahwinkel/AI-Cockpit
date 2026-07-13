namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// What a node is, structurally — which is all the canvas and the engine need to know about it. A trigger starts
/// a run, an action does something, a decision splits the path in two. Everything else about a node (what it
/// actually does) belongs to its type, not to this.
/// </summary>
public enum WorkflowNodeKind
{
    /// <summary>Starts the flow: an event, a schedule, a match in a session's output. Takes nothing in.</summary>
    Trigger,

    /// <summary>Does something: notify, delegate, inject into a session, run a command.</summary>
    Action,

    /// <summary>Splits the path: one way out when its condition holds, another when it does not.</summary>
    Decision,
}
