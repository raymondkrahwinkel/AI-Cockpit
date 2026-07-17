namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Who started a workflow run (#AC-38) — the axis the consent gate turns on. A dangerous step
/// (<see cref="IStepRunner.RequiredConsent"/>) is asked for consent unless the operator started the run themselves.
/// </summary>
public enum RunOrigin
{
    /// <summary>The operator pressed Execute in the editor. Their action is the consent, so a dangerous step runs free.</summary>
    Operator,

    /// <summary>An agent called <c>run_workflow</c> over the MCP. A dangerous step is gated through consent.</summary>
    McpAgent,

    /// <summary>A trigger fired the flow (schedule, text-match, a plugin trigger). A dangerous step is gated unless the flow is marked run-unattended.</summary>
    Trigger,
}
