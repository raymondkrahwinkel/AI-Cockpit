namespace Cockpit.Core.Consent;

/// <summary>
/// The labels the cockpit's own consent-asking callers identify themselves by — and, because a host-internal
/// caller has no plugin id, the keys the assistant's consent bypass (#AC-575) switches are stored under.
/// </summary>
/// <remarks>
/// These constants exist so there is one definition rather than two. The bypass list in Options is filled from
/// here, and the gates below build their <c>ConsentSource</c> from the same constants — so a label that is renamed
/// moves both at once, instead of leaving a switch pointing at a source that no longer answers to that name and a
/// source that quietly stopped being bypassable.
/// <para>
/// Plugins are deliberately absent. A plugin asks through <c>ICockpitHost.RequestConsentAsync</c>, which stamps its
/// plugin id host-side, and that id — not the plugin's own label — is what the bypass keys on. There is no
/// compile-time list of installed plugins, and inventing one here would be a list that goes stale; the Options
/// surface reads them off what has actually asked.
/// </para>
/// </remarks>
public static class ConsentSourceCatalog
{
    /// <summary>The terminal MCP server: running a command in a session's terminal, or taking one over.</summary>
    public const string TerminalMcp = "Terminal MCP";

    /// <summary>The verify MCP server.</summary>
    public const string VerifyMcp = "Verify MCP";

    /// <summary>The worktrees MCP server: creating and removing git worktrees.</summary>
    public const string WorktreesMcp = "Worktrees MCP";

    /// <summary>The delegation orchestrator handing work to a sub-agent.</summary>
    public const string Orchestrator = "Orchestrator";

    /// <summary>The debug-gated sample prompt (#73). Not a real consumer, but it does ask, so it is nameable.</summary>
    public const string Debug = "Debug";

    /// <summary>
    /// The assistant putting a message in another session's inbox: information the recipient reads in its own time.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> the same label as <see cref="AssistantPrompt"/>, and this is the whole reason both
    /// exist. The key is the label (a host-internal caller has no plugin id), so one label would mean one row in
    /// Options and one switch — and telling an agent something would then be un-separable from making it do
    /// something. An operator who is happy for the assistant to leave notes unasked is not thereby happy for it to
    /// start work unasked; a single switch would decide both, and would decide them the permissive way.
    /// </remarks>
    public const string AssistantMessage = "Assistant message";

    /// <summary>The assistant submitting a turn in another session — a hand-off of the operator's own rights.</summary>
    /// <remarks>See <see cref="AssistantMessage"/> for why these are two labels and not one.</remarks>
    public const string AssistantPrompt = "Assistant prompt";

    /// <summary>Every host-internal source, for the bypass list in Options. Ordered as written, which is roughly how often they ask.</summary>
    public static IReadOnlyList<string> HostSources { get; } =
        [TerminalMcp, WorktreesMcp, VerifyMcp, Orchestrator, AssistantMessage, AssistantPrompt, Debug];

    /// <summary>
    /// The bypass key for one source: the host-stamped <paramref name="pluginId"/> under a <c>plugin:</c> prefix, or
    /// the <paramref name="label"/> — a constant above — for a host-internal caller that has no plugin id.
    /// </summary>
    /// <remarks>
    /// The prefix keeps the two halves in separate key spaces. Without it a plugin whose manifest id happens to be
    /// <c>"Terminal MCP"</c> shares a row, and a switch, with the host's own terminal gate: the operator switches one
    /// on and silently arms the other. One definition, used by both the broker (which builds the key a request is
    /// matched on) and the Options list (which builds the key a row is stored under), so the two cannot drift.
    /// <para>
    /// A <c>cockpit.json</c> written before the prefix existed holds bare plugin ids. Those no longer match any
    /// request, so the effect on an existing install is that a plugin's bypass reads as off until the operator ticks
    /// it again — never as on for something it was not set for. The stale keys stay visible: Options lists anything
    /// already switched on, so an orphaned row can still be switched off rather than sitting there unreachable.
    /// </para>
    /// </remarks>
    public static string KeyFor(string? pluginId, string label) =>
        pluginId is null ? label : "plugin:" + pluginId;
}
