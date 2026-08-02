using System.Text.Json.Serialization;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Configuration;

// Root JSON shape of `cockpit.json` under the app config directory. Each store owns one
// section and reads-modifies-writes the whole file so it never clobbers a sibling section: the
// profile store owns `Profiles`, the notification store owns `Notifications`,
// the permission-rule store owns `PermissionRules`, the session-switch store owns
// `SessionSwitching`, the transcript-display store owns `TranscriptDisplay`,
// the layout store owns `Layout`, the voice store owns `Voice`, the
// terminal-settings store owns `Terminal`.
// Kept as a plain DTO separate from the domain records so the on-disk shape can evolve independently.
internal sealed class CockpitConfigFile
{
    // How the credentials in this file are protected: whether encryption is on, and the salt/iterations the
    // key is derived from. Not a secret itself, and deliberately readable before the app is unlocked — without
    // it there is no way to derive the key that reads the rest.
    //
    // Absent unless the operator turned encryption on: encryption is off by default, and a config that says
    // `"Security": null` is a config inviting the question "am I locked?" — which is exactly the question
    // it should never provoke.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretProtectionEntry? Security { get; set; }

    // What the operator has been warned about and dismissed (AC-41) — currently the awareness banner's
    // per-credential-set fingerprint. Owned by `SecretProtectionService`, but declared here so a
    // typed store write round-trips it rather than dropping it. Absent until the banner is first dismissed, and
    // deliberately readable while encryption is off — that is when the banner it silences is shown.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecurityNoticeEntry? SecurityNotice { get; set; }

    // Whether AI-Cockpit locks itself when the OS screen locks (AC-5); owned by the screen-lock-settings store. Its own section, apart from the crypto `Security`, so it outlives turning encryption off and a password change.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScreenLockSettingsEntry? ScreenLock { get; set; }

    public List<SessionProfileEntry> Profiles { get; set; } = [];

    // The Assistant Profile slot (AC-543); owned by the assistant-profile store. Its own section rather than an
    // entry in `Profiles` on purpose: that is what keeps it out of *+ New session* and out of
    // `list_profiles`, and what makes it undeletable — it is not in the list those read.
    public AssistantProfileEntry? AssistantProfile { get; set; }

    public NotificationSettingsEntry? Notifications { get; set; }

    // Always-allow rules keyed by profile label, so each profile keeps its own allowances.
    public Dictionary<string, List<PermissionRuleEntry>> PermissionRules { get; set; } = [];

    public SessionSwitchSettingsEntry? SessionSwitching { get; set; }

    public TranscriptDisplaySettingsEntry? TranscriptDisplay { get; set; }

    // Which metrics the session header's usage pill shows (AC-105); owned by the usage-pill-settings store.
    public UsagePillSettingsEntry? UsagePill { get; set; }

    public SessionBehaviorSettingsEntry? SessionBehavior { get; set; }

    public LayoutSettingsEntry? Layout { get; set; }

    // The operator's own usage thresholds (AC-233), on top of what each provider declared; null when none were ever set.
    public UsageThresholdSettingsEntry? UsageThresholds { get; set; }

    // Prompts waiting to be sent to a session at a future moment (AC-234) — empty for a cockpit with none scheduled.
    public List<ScheduledResumeEntry> ScheduledResumes { get; set; } = [];

    // The terminal-access master switch (AC-34); owned by the terminal-access settings store. Absent/false means the cockpit-terminal MCP is not advertised to any session.
    public TerminalAccessSettingsEntry? TerminalAccess { get; set; }

    // The workspaces and which one is active; owned by the workspace-settings store.
    public WorkspaceSettingsEntry? Workspaces { get; set; }

    // Whether the diagnostic controls are shown (#73); owned by the debug-settings store.
    public DebugSettingsEntry? Debug { get; set; }

    // Delegation settings — whether the orchestrator MCP is offered (AC-40); owned by the delegation-settings store.
    public DelegationSettingsEntry? Delegation { get; set; }

    public VoiceSettingsEntry? Voice { get; set; }

    // The assistant's own on/off/speak/hotkey settings (AC-543); owned by the assistant-settings store. Its own section, apart from the slot in `AssistantProfile`, so switching the feature off never risks the profile it will resume with.
    public AssistantSettingsEntry? Assistant { get; set; }

    // Screenshot capture settings (AC-220) — the desktop-wide key and whether it is armed; owned by the screenshot-settings store.
    public ScreenshotSettingsEntry? Screenshots { get; set; }

    // Global TTY-only terminal appearance (font family/size, #40); owned by the terminal-settings store.
    public TerminalSettingsEntry? Terminal { get; set; }

    // The render backend the operator forced (AC-67); owned by the rendering-settings store. Also read
    // directly, before the container exists, by `RenderBackendConfig` to configure Avalonia at startup.
    public RenderingSettingsEntry? Rendering { get; set; }

    // Plugin enable + consent state (#14) keyed by plugin folder id; owned by the plugin-registration store.
    // Whether the cockpit looks for a newer build of itself, and which builds it will mention (#71).
    public UpdateSettingsEntry? Updates { get; set; }

    public Dictionary<string, PluginRegistrationEntry> Plugins { get; set; } = [];

    // The folder ids of bundled plugins this build has already put in place at least once — the seed-once ledger.
    // A bundled plugin ships as an ordinary, store-updatable plugin: it is copied into the operator's plugins
    // directory on its first appearance and then never touched by the bundle again — the store owns every later
    // version. This is what "first appearance" is measured against, so the seed survives an uninstall (a plugin
    // the operator removed does not silently return next start) and a store update (a newer version the bundle
    // still ships is not rolled back or re-pinned). Mirror of `PluginStoresDefaultSeeded`, per id.
    public List<string> SeededBundledPlugins { get; set; } = [];

    // Per plugin id, the storage keys it keeps a credential in beyond the names the host recognises by itself
    // (a `pat`, a `credential`) — declared in its `plugin.json` or by calling
    // `IPluginStorage.SetSecret`. The names themselves are not secrets, and they have to be readable before
    // the settings are decrypted: they are what says which fields to decrypt.
    public Dictionary<string, List<string>> PluginCredentialFields { get; set; } = [];

    // Configured plugin stores (#14, AC-7) the manager browses — remote (public or private) or local; owned by the plugin-store config store. A bare URL string from a pre-AC-7 config still reads (see `Cockpit.Core.Plugins.PluginStoreConfigJsonConverter`).
    public List<PluginStoreConfig> PluginStores { get; set; } = [];

    // First-run marker (#43) for the built-in default store: set the first time `PluginStores`
    // is resolved, whether that resolution seeded the default store (empty list, unmarked) or merely
    // recognized an existing list as already the operator's own. Once true, the default is never added again —
    // removing the default store is a durable choice, not something the next load undoes.
    public bool PluginStoresDefaultSeeded { get; set; }

    // User-configured MCP servers (#26), shared by the local-LLM tool-loop and the Claude CLI; owned by the MCP-server store.
    public List<McpServerEntry> McpServers { get; set; } = [];

    // Tokens obtained by signing in to the OAuth-protected servers in `McpServers` (AC-353); owned by
    // the MCP OAuth token store. Kept apart from the server entries because the operator rewrites those in full on
    // every edit, which would take a token with it.
    public List<McpOAuthTokenEntry> McpOAuthTokens { get; set; } = [];

    // The operator's projects (AC-158) — what a session works on, beside `Profiles` which is who it works as; owned by the project store.
    public List<ProjectEntry> Projects { get; set; } = [];

    // Ids of shared projects hidden from the Projects workspace on this machine (AC-245) — a per-machine
    // visibility flag, deliberately here and never in a shared project's own definition; owned by the project
    // store.
    public List<string> HiddenSharedProjectIds { get; set; } = [];

    // The project categories' display order and first-typed casing (AC-618); owned by the project store. See `ProjectSettings.CategoryOrder`.
    public List<string> CategoryOrder { get; set; } = [];

    // Remembered working directories (recent + favorites) offered in the New-session dialog; owned by the working-path history store.
    public WorkingPathHistoryEntry? WorkingPaths { get; set; }

    // Keyboard shortcuts for the app actions (new session, options, …); owned by the shortcut settings store.
    public ShortcutSettingsEntry? Shortcuts { get; set; }

    // The main window's last position/size/maximized state; owned by the window-bounds store.
    public WindowBoundsEntry? WindowBounds { get; set; }

    // First-use STT calibration (AC-68 slice 3) keyed by machine name; owned by the transcription-calibration
    // store. Keyed per machine because a config can be synced or restored onto a different box, and a GPU
    // measurement from one machine says nothing about another's.
    public Dictionary<string, TranscriptionCalibrationEntry> TranscriptionCalibrations { get; set; } = [];

    // Git worktrees the cockpit created to isolate sessions (AC-85); owned by the worktree-registry store. The source of truth for cleanup, so it outlives the process that made them.
    public List<WorktreeRegistryEntry> Worktrees { get; set; } = [];

    // Worktree settings (AC-85) — the operator's root-location override; owned by the worktree-settings store. Separate from the `Worktrees` registry above.
    public WorktreeSettingsEntry? WorktreeSettings { get; set; }

    // Repositories cloned from a URL into the managed clones area (AC-90); owned by the repository-clone-registry store. The source of truth for reuse and startup reconciliation, so it outlives the process that cloned them.
    public List<RepositoryCloneEntry> Clones { get; set; } = [];

    // Clone settings (AC-90) — the operator's clones-root-location override; owned by the clone-settings store. Separate from the `Clones` registry above.
    public CloneSettingsEntry? CloneSettings { get; set; }

    // The registered verify runners (AC-86) — the per-project command the visual verify loop may run; owned by the verify-runner-registry store. The agent triggers a runner but never supplies the command, so this list is also the boundary against arbitrary command execution.
    public List<VerifyRunnerEntry> VerifyRunners { get; set; } = [];

    // The first-run wizard's completion marker (AC-509) — the content version the operator has seen, or absent
    // before it has ever run; owned by the first-run-wizard-state store. A version rather than a bool, so a later
    // addition to the wizard can decide whether an install that already finished an earlier version still needs
    // to see something new, without reusing or clearing this flag.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FirstRunWizardVersion { get; set; }
}
