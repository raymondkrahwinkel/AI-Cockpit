namespace Cockpit.Plugin.Workflows.Engine;

// Who started a workflow run (#AC-38) — the axis the consent gate turns on. A dangerous step
// (`IStepRunner.RequiredConsent`) is asked for consent unless the operator started the run themselves.
public enum RunOrigin
{
    // The operator pressed Execute in the editor. Their action is the consent, so a dangerous step runs free.
    Operator,

    // An agent called `run_workflow` over the MCP. A dangerous step is gated through consent.
    McpAgent,

    // A trigger fired the flow (schedule, text-match, a plugin trigger). A dangerous step is gated unless the flow is marked run-unattended.
    Trigger,
}
