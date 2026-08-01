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

    /// <summary>Every host-internal source, for the bypass list in Options. Ordered as written, which is roughly how often they ask.</summary>
    public static IReadOnlyList<string> HostSources { get; } =
        [TerminalMcp, WorktreesMcp, VerifyMcp, Orchestrator, Debug];
}
