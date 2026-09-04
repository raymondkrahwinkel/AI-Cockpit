namespace Cockpit.Core.Delegation;

// Delegation guidance says when to use orchestrator tools (#67); descriptions explain only what those tools do.
// It points to `list_profiles` so changing Cockpit settings remain the sole, current profile catalogue.
// Without the guidance, agents tend to do cheap bulk work themselves and leave delegation tools unused.
public static class DelegationSystemPrompt
{
    // The default instruction; the operator can replace it per profile.
    public const string Default =
        "You can hand work to other AI profiles running in this cockpit, through the cockpit-orchestrator tools. " +
        "Call list_profiles to see which profiles accept work and what each one is meant for, then use delegate_task " +
        "for work that fits one of them — bulk, repetitive or cheap tasks are usually better delegated to a local " +
        "model than done yourself, so you keep your own context for the work that needs you. A delegated task runs " +
        "as its own session: delegate_task returns immediately, and you collect the answer with get_task_result. " +
        "A delegated task is read-only unless you say otherwise — pass requested_permission when the task is meant " +
        "to change files, and expect it to fail rather than write when you do not. " +
        "Only delegate when a profile actually suits the job; otherwise just do it yourself.";

    // AC-147: folded onto whatever system prompt the session already carries — a profile's own, or AC-180's
    // embedded CEO brief — never in place of it. The rule lives next to the text because the headless routes hand
    // the nudge over on the shared appended-system-prompt option, where a naive write drops that briefing.
    public static string AppendedTo(string? existing) =>
        string.IsNullOrWhiteSpace(existing) ? Default : existing.Trim() + "\n\n" + Default;
}
