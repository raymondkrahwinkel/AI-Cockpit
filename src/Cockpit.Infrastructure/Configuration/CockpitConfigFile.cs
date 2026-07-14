using System.Text.Json.Serialization;

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
    /// <summary>
    /// How the credentials in this file are protected: whether encryption is on, and the salt/iterations the
    /// key is derived from. Not a secret itself, and deliberately readable before the app is unlocked — without
    /// it there is no way to derive the key that reads the rest.
    /// <para>
    /// Absent unless the operator turned encryption on: encryption is off by default, and a config that says
    /// <c>"Security": null</c> is a config inviting the question "am I locked?" — which is exactly the question
    /// it should never provoke.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretProtectionEntry? Security { get; set; }

    public List<SessionProfileEntry> Profiles { get; set; } = [];

    public NotificationSettingsEntry? Notifications { get; set; }

    /// <summary>Always-allow rules keyed by profile label, so each profile keeps its own allowances.</summary>
    public Dictionary<string, List<PermissionRuleEntry>> PermissionRules { get; set; } = [];

    public SessionSwitchSettingsEntry? SessionSwitching { get; set; }

    public TranscriptDisplaySettingsEntry? TranscriptDisplay { get; set; }

    public SessionBehaviorSettingsEntry? SessionBehavior { get; set; }

    public LayoutSettingsEntry? Layout { get; set; }

    /// <summary>Whether the diagnostic controls are shown (#73); owned by the debug-settings store.</summary>
    public DebugSettingsEntry? Debug { get; set; }

    public VoiceSettingsEntry? Voice { get; set; }

    /// <summary>Global TTY-only terminal appearance (font family/size, #40); owned by the terminal-settings store.</summary>
    public TerminalSettingsEntry? Terminal { get; set; }

    /// <summary>Plugin enable + consent state (#14) keyed by plugin folder id; owned by the plugin-registration store.</summary>
    /// <summary>Whether the cockpit looks for a newer build of itself, and which builds it will mention (#71).</summary>
    public UpdateSettingsEntry? Updates { get; set; }

    public Dictionary<string, PluginRegistrationEntry> Plugins { get; set; } = [];

    /// <summary>
    /// Per plugin id, the storage keys it keeps a credential in beyond the names the host recognises by itself
    /// (a <c>pat</c>, a <c>credential</c>) — declared in its <c>plugin.json</c> or by calling
    /// <c>IPluginStorage.SetSecret</c>. The names themselves are not secrets, and they have to be readable before
    /// the settings are decrypted: they are what says which fields to decrypt.
    /// </summary>
    public Dictionary<string, List<string>> PluginCredentialFields { get; set; } = [];

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

    /// <summary>Remembered working directories (recent + favorites) offered in the New-session dialog; owned by the working-path history store.</summary>
    public WorkingPathHistoryEntry? WorkingPaths { get; set; }

    /// <summary>Keyboard shortcuts for the app actions (new session, options, …); owned by the shortcut settings store.</summary>
    public ShortcutSettingsEntry? Shortcuts { get; set; }

    /// <summary>The main window's last position/size/maximized state; owned by the window-bounds store.</summary>
    public WindowBoundsEntry? WindowBounds { get; set; }
}
