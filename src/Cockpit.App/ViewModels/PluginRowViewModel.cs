using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

// One row in the plugin manager (#14): the display fields plus the action affordances derived from the
// plugin's `PluginLoadDecision`. The manager owns the enable/disable/remove commands and
// takes the row as their parameter, so the row itself stays a passive projection of an installed plugin.
public sealed class PluginRowViewModel(InstalledPlugin installed, bool hasSettings = false)
{
    public DiscoveredPlugin Discovered => installed.Discovered;

    // Whether this plugin's left-menu contributions are hidden (#72). The plugin still runs — its shortcut and command-palette entry keep working — which is what separates this from disabling it.
    public bool HiddenInMenu => installed.Registration?.HiddenInMenu ?? false;

    // The eye toggle's label, which has to name the action rather than the state: a toggle that reads "Hidden" leaves you guessing what clicking it does.
    public string MenuVisibilityLabel => HiddenInMenu ? "Show in menu" : "Hide from menu";

    // Spells out what hiding does and does not do, since "hidden" reading as "off" is the trap here.
    public string MenuVisibilityTip => HiddenInMenu
        ? "Show this plugin's buttons and sections in the left menu again."
        : "Keep this plugin's buttons and sections out of the left menu. The plugin keeps running: its shortcut and command-palette entry still work — that is the difference with disabling it.";

    // Whether this plugin's contributions show top-level in the sidebar rather than collapsed under "Plugins ›" (AC-937).
    public bool PinnedToSidebar => installed.Registration?.PinnedToSidebar ?? false;

    // The pin toggle's label names the action, the same way MenuVisibilityLabel above does.
    public string PinToggleLabel => PinnedToSidebar ? "Unpin from sidebar" : "Pin to sidebar";

    public string PinToggleTip => PinnedToSidebar
        ? "Move this plugin's buttons and sections into the collapsed \"Plugins ›\" menu."
        : "Show this plugin's buttons and sections directly in the sidebar instead of collapsed under \"Plugins ›\".";

    // True when the loaded plugin registered a settings view (#14) — the manager shows a gear to open it.
    public bool HasSettings => hasSettings;

    // True when this plugin never became operative (load/configure/initialize), or is flagged for a compatibility concern.
    public bool HasFailure => installed.ActivationFailure is not null || installed.CompatibilityWarning is not null;

    // The load/init failure or compatibility warning for this plugin, if any (#14) — a contribution failing later (#184) is a separate fact, see `McpContributionFailureText`.
    public string FailureText => installed.ActivationFailure is { } activation
        ? $"Failed to load: {activation}"
        : installed.CompatibilityWarning ?? string.Empty;

    // True when this plugin loaded but a contribution it registered afterwards failed (#184) — e.g. its MCP server upsert. Independent of `HasFailure`: the plugin is still running.
    public bool HasMcpContributionFailure => installed.McpContributionFailure is not null;

    public string McpContributionFailureText => installed.McpContributionFailure is { } mcp
        ? $"Its MCP server contribution failed: {mcp}"
        : string.Empty;

    public string FolderId => Discovered.FolderId;

    public string DisplayName => Discovered.Manifest.Name;

    public string Version => $"v{Discovered.Manifest.Version}";

    public string? Author => Discovered.Manifest.Author;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(Discovered.Manifest.Author);

    public string Description => Discovered.Manifest.Description ?? "No description provided.";

    public string StatusText => Discovered.Decision switch
    {
        // Load alone is a discovery-time decision (#184) — a plugin that threw while loading, configuring or
        // initializing never became operative even though it was decided to load, and reporting it as enabled
        // would say the opposite of what happened.
        PluginLoadDecision.Load when installed.ActivationFailure is not null => "Failed to load — see below",
        PluginLoadDecision.Load => "Enabled — active this session",
        PluginLoadDecision.Disabled => "Disabled",
        PluginLoadDecision.NeedsConsent => "Needs your consent",
        PluginLoadDecision.AbstractionsMajorMismatch => "Incompatible — built for another contract version",
        // Named in full here, unlike the rest of the running text: the sentence carries two version numbers'
        // worth of ambiguity otherwise — the reader cannot tell whether it is the plugin's version or the host's.
        PluginLoadDecision.HostTooOld => $"Needs {CockpitProduct.DisplayName} {Discovered.Manifest.MinHostVersion} or later",
        _ => string.Empty,
    };

    // The plugin can be enabled (it is disabled or awaiting consent) — enabling always shows the consent dialog.
    public bool CanEnable => Discovered.Decision is PluginLoadDecision.Disabled or PluginLoadDecision.NeedsConsent;

    // The plugin is enabled and consented, so the only state change offered is to disable it.
    public bool CanDisable => Discovered.Decision is PluginLoadDecision.Load;

    // A version-incompatible plugin cannot be enabled at all — the manager shows why instead of an Enable button.
    public bool IsIncompatible =>
        Discovered.Decision is PluginLoadDecision.AbstractionsMajorMismatch or PluginLoadDecision.HostTooOld;

    public string EnableLabel => Discovered.Decision is PluginLoadDecision.NeedsConsent ? "Review & enable" : "Enable";

    public PluginConsentInfo ToConsentInfo() => new(
        Discovered.Manifest.Name,
        Discovered.Manifest.Version,
        Discovered.Manifest.Author,
        Discovered.FolderPath,
        Discovered.Sha256);
}
