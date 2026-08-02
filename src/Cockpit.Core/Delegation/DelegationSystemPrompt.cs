namespace Cockpit.Core.Delegation;

// The instruction handed to a session that can delegate (#67). Tool descriptions tell a model *what* the
// orchestrator tools do; they say nothing about *when* reaching for them is a good idea. Without that, an
// agent that could hand cheap bulk work to a local model simply does it itself — the tools sit there unused.
// Deliberately points at `list_profiles` rather than naming the profiles: the profiles and what they are
// good for live in the cockpit's own settings and change there, so restating them here would only give the model
// a second, staler copy.
public static class DelegationSystemPrompt
{
    // The default instruction; the operator can replace it per profile.
    public const string Default =
        "You can hand work to other AI profiles running in this cockpit, through the cockpit-orchestrator tools. " +
        "Call list_profiles to see which profiles accept work and what each one is meant for, then use delegate_task " +
        "for work that fits one of them — bulk, repetitive or cheap tasks are usually better delegated to a local " +
        "model than done yourself, so you keep your own context for the work that needs you. A delegated task runs " +
        "as its own session: delegate_task returns immediately, and you collect the answer with get_task_result. " +
        "Only delegate when a profile actually suits the job; otherwise just do it yourself.";
}
