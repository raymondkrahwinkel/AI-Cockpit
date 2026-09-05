namespace Cockpit.Core.Consent;

// Host-internal consent labels live here so gates and Options bypass keys cannot drift (#AC-575).
// Plugins are absent: the host stamps their id, and Options discovers installed plugins from actual requests.
// Renaming a constant therefore moves both the requesting source and its stored bypass key together.
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

    // The whiteboard's own "Laat sdk meekijken" button (AC-842), kept apart from WhiteboardMcp so the audit trail
    // can tell the operator's own invite from the agent asking for itself. Never in `HostSources` (AC-888): it
    // calls `RequestConsentAsync` outside any MCP request, so the bypass never even gets asked.
    public const string WhiteboardInvite = "Whiteboard invite";

    // The verify MCP server.
    public const string VerifyMcp = "Verify MCP";

    // The worktrees MCP server: creating and removing git worktrees.
    public const string WorktreesMcp = "Worktrees MCP";

    // The delegation orchestrator handing work to a sub-agent.
    public const string Orchestrator = "Orchestrator";

    // The debug-gated sample prompt (#73). Not a real consumer, but it does ask, so it is nameable.
    public const string Debug = "Debug";

    // Assistant inbox messages have their own consent label: a host-internal caller has no plugin id (AC-798).
    // Sharing `AssistantPrompt` would make notes and starting work one permissive, inseparable Options switch.
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

    // Adding a colleague's shared project has its own label (AC-798), not consent for every assistant write.
    public const string AssistantProjectBinding = "Assistant project binding";

    // The assistant creating a brand-new local project (AC-799) — its own label, not `AssistantProjectBinding`:
    // that one registers a colleague's own definition, this one writes fields the assistant composed itself
    // (a behaviour prompt among them), and an operator may trust one without the other.
    public const string AssistantProjectCreate = "Assistant project create";

    // The assistant changing an existing project's fields (AC-1059) — its own label, not `AssistantProjectCreate`:
    // that one writes a record nothing yet depends on, this one can change what a session already running there
    // inherits on its next spawn, so an operator may trust one without the other.
    public const string AssistantProjectUpdate = "Assistant project update";

    // The assistant opening a web address in the operator's browser (AC-587) — arbitrary egress, and its own
    // label for the same reason every other assistant source above has one: an operator who lets the assistant
    // leave notes or hand off a session unasked has not thereby agreed to it reaching out to any URL it names.
    public const string AssistantOpenUrl = "Assistant open URL";

    // Every host-internal source, for the bypass list in Options. Ordered as written, which is roughly how often they ask.
    // Plugin rows are absent on purpose, `WhiteboardInvite` included — see its own comment above.
    public static IReadOnlyList<string> HostSources { get; } =
    [
        TerminalMcp, DiagramMcp, WhiteboardMcp, WireframeMcp, WorktreesMcp, VerifyMcp, Orchestrator, AssistantMessage, AssistantPrompt,
        AssistantMemoryExport, AssistantMemoryImport, AssistantProjectBinding, AssistantProjectCreate, AssistantProjectUpdate, AssistantOpenUrl, Debug,
    ];

    // The bypass key: the host-stamped `pluginId` and the caller's own `label` under a `plugin:` prefix, or the
    // bare `label` for a host-internal caller. `pluginId` is a folder name (AC-888) and so can never contain a
    // `/`, which is what keeps a plugin's own choice of `label` from ever reaching another key space.
    public static string KeyFor(string? pluginId, string label) =>
        pluginId is null ? label : $"plugin:{pluginId}/{label}";
}
