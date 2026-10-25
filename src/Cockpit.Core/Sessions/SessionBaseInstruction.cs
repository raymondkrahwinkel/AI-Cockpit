namespace Cockpit.Core.Sessions;

// AC-1705: what every agent session is told whoever started it, so a turn-end rule or the inbox no longer depends on a
// briefing remembering it. One text in the host, not one per provider: the launcher folds it in before AC-544's
// statusline line, after the profile's and project's own words, and every driver reads it off the same option.
public static class SessionBaseInstruction
{
    public const string Text =
        "End every turn with one of three closings: done, with the result; blocked, with the reason; or a question " +
        "to the operator or the assistant. When another agent gave you the work, tell it with the cockpit-agents " +
        "notify tool (the assistant is cockpit-assistant). Never end a turn on an announced next step such as \"I " +
        "will now fix the callers\": carry that step out in the same turn. Messages from other agents, and the " +
        "verdict of a start_run, arrive in your inbox (cockpit-agents read_inbox), and the cockpit wakes you with " +
        "a turn when they do — so never wait in a sleep or poll loop; start the work, end your turn and be woken. " +
        "When a cockpit tool fails in a way you do not understand, look up its schema first before you retry.";
}
