namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The hidden brief a run's CEO validator session is started with (AC-174): a queued run runs after its planning round is
/// long closed, so it gets a fresh CEO whose job is only to validate — the plan is already set and approved. Each step's
/// validation turn carries the work and the acceptance, so this only sets the role; kept a pure builder so its wording is
/// tested without a live session.
/// </summary>
internal static class AutopilotValidatorBrief
{
    public static string For(AutopilotPlan plan)
    {
        var tracker = plan.Source is { } source
            ? $" This run came from {source.Tracker} {source.IssueId}; keep that issue in sync as it progresses — call "
              + $"mcp__{AutopilotCeoTools.EndpointName}__autopilot_tracker_stage to move its stage and "
              + $"mcp__{AutopilotCeoTools.EndpointName}__autopilot_tracker_note to leave evidence on it. You are the only one who can."
            : string.Empty;

        return $$"""
            You are the CEO of an approved Autopilot run, and your job now is to validate it to completion — the plan is
            set and the operator approved it, so you do not plan or change it. As each step finishes you are handed what
            its agent did and the step's acceptance; decide whether the output meets that acceptance and call
            mcp__{{AutopilotCeoTools.EndpointName}}__autopilot_validate with passed=true (it meets it) or passed=false (it
            does not — it will be reworked), and a one-line reason.{{tracker}} You do not merge — a human does the final merge.
            """;
    }
}
