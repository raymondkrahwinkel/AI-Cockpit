namespace Cockpit.Core.Sessions;

/// <summary>
/// The standing instruction every ordinary spun-up agent starts with, telling it to keep its own statusline
/// current (AC-544). Tool descriptions tell a model <em>what</em> the cockpit-session tools do; they say nothing
/// about <em>when</em> reaching for them is worth it. <c>set_status</c>'s own description already says "update it
/// as you move on" — but a description is read once, at tool-choice time, and is not carried through the rest of
/// the session as a standing habit. Without this, an agent that set its status when it picked up a ticket is still
/// showing that same line an hour later, mid something else entirely: only the agent itself knows the phase of its
/// own work has changed, and nothing prompts it to say so again.
/// </summary>
/// <remarks>
/// Not the voice assistant's own prompt (<see cref="Cockpit.Core.Assistant.AssistantSystemPrompt"/>) — that one is
/// a different product surface for a different audience (spoken, one-to-one, no tools of its own to nudge). This
/// one is for the sessions the cockpit spins up with a pane: a New-session window and a project quick-start, both
/// of which get a statusline the operator glances at, and neither of which gets told to keep it current unless
/// something says so.
/// <para>
/// <b>Not a delegated sub-agent, and that is a gap rather than a decision.</b> <c>DelegationService</c> builds its
/// own launch options carrying only the pane id and never sets the append-system-prompt key at all, so a delegated
/// task reaches neither this instruction nor its profile's own identity prompt (the older half of that gap is
/// AC-142's). It also has no <c>SessionPanelViewModel</c> and therefore no statusline to keep, so the instruction
/// would currently be an order it could not carry out. Written down here rather than left for the next reader to
/// rediscover from the absence.
/// </para>
/// </remarks>
public static class AgentStatusSystemPrompt
{
    /// <summary>
    /// The default instruction. <c>NewSessionResult</c> puts it on every launch it composes, after a profile's own
    /// <c>SystemPrompt</c> (AC-142) when there is one rather than instead of it — the arrangement AC-544's criterion
    /// 5 points at by name. The delegation instruction works exactly this way already: it travels as its own value
    /// and <c>ClaudeTtyProvider._AppendedInstructions</c> joins it to whatever the profile resolved, so a profile
    /// with an identity still gets the orchestrator nudge.
    /// <para>
    /// The alternative — a written profile prompt displacing this one — reads like deference to the profile author
    /// and is the wrong way round in practice. A profile carrying a deliberate identity is a considered profile,
    /// which is to say one doing real ticket work, so it is precisely the session whose statusline the operator
    /// most wants kept. Displacement would switch the instruction off exactly there, quietly, as a side effect of
    /// writing an unrelated sentence.
    /// </para>
    /// <para>
    /// Overridable per profile in the way a system prompt is overridable at all: the profile's words come first,
    /// and an operator who wants different behaviour writes it. What no profile can do is lose this by accident.
    /// </para>
    /// </summary>
    public const string Default =
        "Keep your own statusline current with the cockpit-session set_status tool, so the operator can see what " +
        "you are doing without opening your session. Set it as soon as you pick up a ticket or task, naming it " +
        "plainly (\"AC-544\", \"reviewing the diff\"), and update it whenever the phase of your work changes — " +
        "reading, writing, running tests — since only you know when that has happened. Clear it when the work " +
        "is done.";
}
