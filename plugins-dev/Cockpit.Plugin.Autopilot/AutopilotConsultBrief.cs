namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The turn a run's CEO is handed when a step worker consults it mid-step (AC-201, spoor 2): the worker's question, the
/// step it is on, and the two ways out — answer the worker or escalate to the operator. Kept a pure builder off the
/// coordinator (mirroring <see cref="AutopilotStepBrief.ValidationTurn"/>) so the wording — the tools to call, what to
/// include — is tested without a live session. Relayed into the CEO's own session through <c>host.SendToSessionAsync</c>.
/// </summary>
internal static class AutopilotConsultBrief
{
    public static string ConsultTurn(AutopilotStep? step, string question)
    {
        var title = step is { } current ? current.Title : "(the running step)";

        return $$"""
            A worker on the step you are overseeing is consulting you before it continues — answer it, or escalate it to
            the operator. Step: {{title}}.

            The worker asks:
            {{question}}

            You may inspect the working directory (Read/Grep) to decide. If you can settle it yourself — a convention to
            follow, a reasonable default, a design call within the approved plan — answer with
            mcp__{{AutopilotCeoTools.EndpointName}}__autopilot_answer_worker; your answer is relayed to the worker as a
            turn in its session and it carries on. Only if it genuinely needs the operator — a truly irreversible or
            destructive choice, a missing credential, or a business preference you cannot make within the plan — escalate
            with mcp__{{AutopilotCeoTools.EndpointName}}__autopilot_escalate_to_operator. Prefer to answer yourself
            whenever you reasonably can.
            """;
    }
}
