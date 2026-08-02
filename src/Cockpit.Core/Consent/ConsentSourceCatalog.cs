namespace Cockpit.Core.Consent;

// The labels the cockpit's own consent-asking callers identify themselves by — and, because a host-internal
// caller has no plugin id, the keys the assistant's consent bypass (#AC-575) switches are stored under.
// These constants exist so there is one definition rather than two. The bypass list in Options is filled from
// here, and the gates below build their `ConsentSource` from the same constants — so a label that is renamed
// moves both at once, instead of leaving a switch pointing at a source that no longer answers to that name and a
// source that quietly stopped being bypassable.
//
// Plugins are deliberately absent. A plugin asks through `ICockpitHost.RequestConsentAsync`, which stamps its
// plugin id host-side, and that id — not the plugin's own label — is what the bypass keys on. There is no
// compile-time list of installed plugins, and inventing one here would be a list that goes stale; the Options
// surface reads them off what has actually asked.
public static class ConsentSourceCatalog
{
    // The terminal MCP server: running a command in a session's terminal, or taking one over.
    public const string TerminalMcp = "Terminal MCP";

    // The verify MCP server.
    public const string VerifyMcp = "Verify MCP";

    // The worktrees MCP server: creating and removing git worktrees.
    public const string WorktreesMcp = "Worktrees MCP";

    // The delegation orchestrator handing work to a sub-agent.
    public const string Orchestrator = "Orchestrator";

    // The debug-gated sample prompt (#73). Not a real consumer, but it does ask, so it is nameable.
    public const string Debug = "Debug";

    // The assistant putting a message in another session's inbox: information the recipient reads in its own time.
    // Deliberately *not* the same label as `AssistantPrompt`, and this is the whole reason both
    // exist. The key is the label (a host-internal caller has no plugin id), so one label would mean one row in
    // Options and one switch — and telling an agent something would then be un-separable from making it do
    // something. An operator who is happy for the assistant to leave notes unasked is not thereby happy for it to
    // start work unasked; a single switch would decide both, and would decide them the permissive way.
    public const string AssistantMessage = "Assistant message";

    // The assistant submitting a turn in another session — a hand-off of the operator's own rights.
    // See `AssistantMessage` for why these are two labels and not one.
    public const string AssistantPrompt = "Assistant prompt";

    // Every host-internal source, for the bypass list in Options. Ordered as written, which is roughly how often they ask.
    public static IReadOnlyList<string> HostSources { get; } =
        [TerminalMcp, WorktreesMcp, VerifyMcp, Orchestrator, AssistantMessage, AssistantPrompt, Debug];

    // The bypass key for one source: the host-stamped `pluginId` under a `plugin:` prefix, or
    // the `label` — a constant above — for a host-internal caller that has no plugin id.
    // The prefix keeps the two halves in separate key spaces. Without it a plugin whose manifest id happens to be
    // `"Terminal MCP"` shares a row, and a switch, with the host's own terminal gate: the operator switches one
    // on and silently arms the other. One definition, used by both the broker (which builds the key a request is
    // matched on) and the Options list (which builds the key a row is stored under), so the two cannot drift.
    //
    // A `cockpit.json` written before the prefix existed holds bare plugin ids. Those no longer match any
    // request, so the effect on an existing install is that a plugin's bypass reads as off until the operator ticks
    // it again — never as on for something it was not set for. The stale keys stay visible: Options lists anything
    // already switched on, so an orphaned row can still be switched off rather than sitting there unreachable.
    public static string KeyFor(string? pluginId, string label) =>
        pluginId is null ? label : "plugin:" + pluginId;
}
