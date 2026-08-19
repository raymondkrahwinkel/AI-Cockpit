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

    // The diagram MCP server (AC-810): reading or editing a diagram surface the operator has open.
    public const string DiagramMcp = "Diagram MCP";

    // The whiteboard MCP server (AC-823): reading a screenshot of a whiteboard surface the operator has open.
    public const string WhiteboardMcp = "Whiteboard MCP";

    // The wireframe MCP server (AC-872): reading or editing a wireframe surface the operator has open.
    public const string WireframeMcp = "Wireframe MCP";

    // The whiteboard's own "Laat sdk meekijken" button (AC-842): the operator inviting the coupled session's agent
    // to read the board, kept apart from WhiteboardMcp so the audit trail can tell the operator's own invite from
    // the agent asking for itself. Not in `HostSources` and never will be (AC-888): the button calls
    // `RequestConsentAsync` directly from the UI, outside any MCP request, so `McpRequestContext.CurrentPaneId`
    // is null and the bypass never even gets asked — a switch for this row could not do anything.
    public const string WhiteboardInvite = "Whiteboard invite";

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

    // The assistant exporting its own memory files (AC-657) to a path it names. Read-only towards the assistant's
    // own state, so it is the lighter of the pair — same reasoning as AssistantMessage vs AssistantPrompt: being
    // fine with a copy going out is not the same as being fine with the live memory being overwritten.
    public const string AssistantMemoryExport = "Assistant memory export";

    // The assistant importing its memory files (AC-657) from an archive, replacing what is live. See
    // `AssistantMemoryExport`.
    public const string AssistantMemoryImport = "Assistant memory import";

    // The assistant adding a project a colleague shares (AC-798) to this machine — its own label rather than one
    // shared with the assistant's other writes, for the same reason `AssistantMessage` and `AssistantPrompt` are
    // two: an operator happy for their team's projects to be added unasked has not thereby agreed to anything else
    // the assistant writes.
    public const string AssistantProjectBinding = "Assistant project binding";

    // The assistant creating a brand-new local project (AC-799) — its own label, not `AssistantProjectBinding`:
    // that one registers a colleague's own definition, this one writes fields the assistant composed itself
    // (a behaviour prompt among them), and an operator may trust one without the other.
    public const string AssistantProjectCreate = "Assistant project create";

    // Every host-internal source, for the bypass list in Options. Ordered as written, which is roughly how often they ask.
    // Plugin rows are absent on purpose, `WhiteboardInvite` included — see its own comment above.
    public static IReadOnlyList<string> HostSources { get; } =
    [
        TerminalMcp, DiagramMcp, WhiteboardMcp, WireframeMcp, WorktreesMcp, VerifyMcp, Orchestrator, AssistantMessage, AssistantPrompt,
        AssistantMemoryExport, AssistantMemoryImport, AssistantProjectBinding, AssistantProjectCreate, Debug,
    ];

    // The bypass key for one source: the host-stamped `pluginId` and the caller's own `label` under a `plugin:`
    // prefix, or the bare `label` — a constant above — for a host-internal caller that has no plugin id.
    // The prefix keeps a plugin's whole key space separate from the host's and from every other plugin's
    // (AC-888): `pluginId` is stamped from the plugin's folder name (`PluginDiscovery.FolderId`), which cannot
    // contain a `/` on any supported filesystem, so the first `/` after `plugin:` is always the id/label
    // boundary — a plugin's own choice of `label` can only add rows inside its own space, never reach into the
    // host's or another plugin's. One definition, used by both the broker (which builds the key a request is
    // matched on) and the Options list (which builds the key a row is stored under), so the two cannot drift.
    //
    // A `cockpit.json` written before the label joined the key (or, further back, before the prefix existed)
    // holds a shorter key. Those no longer match any request, so the effect on an existing install is that a
    // plugin's bypass reads as off until the operator ticks it again — never as on for something it was not set
    // for. The stale keys stay visible: Options lists anything already switched on, so an orphaned row can still
    // be switched off rather than sitting there unreachable.
    public static string KeyFor(string? pluginId, string label) =>
        pluginId is null ? label : $"plugin:{pluginId}/{label}";
}
