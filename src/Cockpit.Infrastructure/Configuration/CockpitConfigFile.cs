namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Root JSON shape of <c>cockpit.json</c> under the app config directory. Each store owns one
/// section and reads-modifies-writes the whole file so it never clobbers a sibling section: the
/// profile store owns <see cref="Profiles"/>, the notification store owns <see cref="Notifications"/>,
/// the permission-rule store owns <see cref="PermissionRules"/>, the session-switch store owns
/// <see cref="SessionSwitching"/>, the transcript-display store owns <see cref="TranscriptDisplay"/>,
/// the layout store owns <see cref="Layout"/>, the voice store owns <see cref="Voice"/>, the
/// terminal-settings store owns <see cref="Terminal"/>.
/// Kept as a plain DTO separate from the domain records so the on-disk shape can evolve independently.
/// </summary>
internal sealed class CockpitConfigFile
{
    public List<ClaudeProfileEntry> Profiles { get; set; } = [];

    public NotificationSettingsEntry? Notifications { get; set; }

    /// <summary>Always-allow rules keyed by profile label, so each profile keeps its own allowances.</summary>
    public Dictionary<string, List<PermissionRuleEntry>> PermissionRules { get; set; } = [];

    public SessionSwitchSettingsEntry? SessionSwitching { get; set; }

    public TranscriptDisplaySettingsEntry? TranscriptDisplay { get; set; }

    public SessionBehaviorSettingsEntry? SessionBehavior { get; set; }

    public LayoutSettingsEntry? Layout { get; set; }

    public VoiceSettingsEntry? Voice { get; set; }

    /// <summary>Global TTY-only terminal appearance (font family/size, #40); owned by the terminal-settings store.</summary>
    public TerminalSettingsEntry? Terminal { get; set; }

    /// <summary>Plugin enable + consent state (#14) keyed by plugin folder id; owned by the plugin-registration store.</summary>
    public Dictionary<string, PluginRegistrationEntry> Plugins { get; set; } = [];

    /// <summary>Configured plugin-store URLs (#14) the manager browses for installable plugins; owned by the plugin-store config store.</summary>
    public List<string> PluginStores { get; set; } = [];

    /// <summary>
    /// First-run marker (#43) for the built-in default store: set the first time <see cref="PluginStores"/>
    /// is resolved, whether that resolution seeded the default store (empty list, unmarked) or merely
    /// recognized an existing list as already the operator's own. Once true, the default is never added again —
    /// removing the default store is a durable choice, not something the next load undoes.
    /// </summary>
    public bool PluginStoresDefaultSeeded { get; set; }

    /// <summary>User-configured MCP servers (#26), shared by the local-LLM tool-loop and the Claude CLI; owned by the MCP-server store.</summary>
    public List<McpServerEntry> McpServers { get; set; } = [];
}
