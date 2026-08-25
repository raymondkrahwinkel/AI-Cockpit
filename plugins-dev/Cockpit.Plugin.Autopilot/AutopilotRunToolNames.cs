namespace Cockpit.Plugin.Autopilot;

// Fully-qualified names (AC-215) of Autopilot's own in-process tools, pre-authorized on a run's embedded
// sessions so it does not stop mid-run to ask the operator for a tool it depends on. Only the plugin's own
// endpoint tools — never file, shell or egress tools, which stay gated by permission mode and ConsentBroker.
internal static class AutopilotRunToolNames
{
    private static string Qualify(string endpoint, string tool) => $"mcp__{endpoint}__{tool}";

    // The step endpoint tools a step worker is pre-approved for: report done, and consult the CEO (AC-201).
    public static IReadOnlyList<string> ForStepWorker { get; } =
    [
        Qualify(AutopilotRunTools.EndpointName, "autopilot_step_done"),
        Qualify(AutopilotRunTools.EndpointName, "autopilot_blocked"),
    ];

    // The CEO endpoint tools the run's validator CEO is pre-approved for: validate, answer/escalate a worker, and move/annotate the tracker.
    public static IReadOnlyList<string> ForValidatorCeo { get; } =
    [
        Qualify(AutopilotCeoTools.EndpointName, "autopilot_validate"),
        Qualify(AutopilotCeoTools.EndpointName, "autopilot_answer_worker"),
        Qualify(AutopilotCeoTools.EndpointName, "autopilot_escalate_to_operator"),
        Qualify(AutopilotCeoTools.EndpointName, "autopilot_tracker_stage"),
        Qualify(AutopilotCeoTools.EndpointName, "autopilot_tracker_note"),
    ];
}
