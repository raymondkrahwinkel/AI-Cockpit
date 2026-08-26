namespace Cockpit.Core.Sessions;

// AC-544: standing statusline instruction for spun-up agents — a tool description is read once at
// tool-choice time, not carried as a habit through the session. Not the voice assistant's prompt (different
// surface); not reached by delegated sub-agents (AC-142, `DelegationService` gap, not a decision).
public static class AgentStatusSystemPrompt
{
    // AC-544 criterion 5: appended after a profile's own `SystemPrompt` (AC-142), never displacing it —
    // a profile with a deliberate identity is exactly the session whose statusline matters most to keep.
    // Overridable in the normal system-prompt sense: the profile's words come first, but nothing loses this.
    public const string Default =
        "Keep your own statusline current with the cockpit-session set_status tool, so the operator can see what " +
        "you are doing without opening your session. Set it as soon as you pick up a ticket or task, naming it " +
        "plainly (\"AC-544\", \"reviewing the diff\"), and update it whenever the phase of your work changes — " +
        "reading, writing, running tests — since only you know when that has happened. Clear it when the work " +
        "is done. CI reports arrive when a check fails or a pull request becomes mergeable; a running check sends " +
        "no message, so silence is not a green result.";
}
