using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One row in the plugin manager (#14): the display fields plus the action affordances derived from the
/// plugin's <see cref="PluginLoadDecision"/>. The manager owns the enable/disable/remove commands and
/// takes the row as their parameter, so the row itself stays a passive projection of a discovered plugin.
/// </summary>
public sealed class PluginRowViewModel(DiscoveredPlugin discovered, bool hasSettings = false, string? failureError = null, bool hiddenInMenu = false)
{
    public DiscoveredPlugin Discovered => discovered;

    /// <summary>Whether this plugin's left-menu contributions are hidden (#72). The plugin still runs — its shortcut and command-palette entry keep working — which is what separates this from disabling it.</summary>
    public bool HiddenInMenu => hiddenInMenu;

    /// <summary>The eye toggle's label, which has to name the action rather than the state: a toggle that reads "Hidden" leaves you guessing what clicking it does.</summary>
    public string MenuVisibilityLabel => hiddenInMenu ? "Show in menu" : "Hide from menu";

    /// <summary>Spells out what hiding does and does not do, since "hidden" reading as "off" is the trap here.</summary>
    public string MenuVisibilityTip => hiddenInMenu
        ? "Show this plugin's buttons and sections in the left menu again."
        : "Keep this plugin's buttons and sections out of the left menu. The plugin keeps running: its shortcut and command-palette entry still work — that is the difference with disabling it.";

    /// <summary>True when the loaded plugin registered a settings view (#14) — the manager shows a gear to open it.</summary>
    public bool HasSettings => hasSettings;

    /// <summary>The load/init error for this plugin, if it failed (#14); shown in red on the row so a broken plugin is visible in the manager.</summary>
    public string? FailureError => failureError;

    /// <summary>True when this plugin failed to load or initialize.</summary>
    public bool HasFailure => !string.IsNullOrEmpty(failureError);

    public string FailureText => $"⚠ Failed to load: {failureError}";

    public string FolderId => discovered.FolderId;

    public string DisplayName => discovered.Manifest.Name;

    public string Version => $"v{discovered.Manifest.Version}";

    public string? Author => discovered.Manifest.Author;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(discovered.Manifest.Author);

    public string Description => discovered.Manifest.Description ?? "No description provided.";

    public string StatusText => discovered.Decision switch
    {
        PluginLoadDecision.Load => "Enabled — active this session",
        PluginLoadDecision.Disabled => "Disabled",
        PluginLoadDecision.NeedsConsent => "Needs your consent",
        PluginLoadDecision.AbstractionsMajorMismatch => "Incompatible — built for another contract version",
        PluginLoadDecision.HostTooOld => $"Needs a newer cockpit (version {discovered.Manifest.MinHostVersion} or later)",
        _ => string.Empty,
    };

    /// <summary>The plugin can be enabled (it is disabled or awaiting consent) — enabling always shows the consent dialog.</summary>
    public bool CanEnable => discovered.Decision is PluginLoadDecision.Disabled or PluginLoadDecision.NeedsConsent;

    /// <summary>The plugin is enabled and consented, so the only state change offered is to disable it.</summary>
    public bool CanDisable => discovered.Decision is PluginLoadDecision.Load;

    /// <summary>A version-incompatible plugin cannot be enabled at all — the manager shows why instead of an Enable button.</summary>
    public bool IsIncompatible =>
        discovered.Decision is PluginLoadDecision.AbstractionsMajorMismatch or PluginLoadDecision.HostTooOld;

    public string EnableLabel => discovered.Decision is PluginLoadDecision.NeedsConsent ? "Review & enable" : "Enable";

    public PluginConsentInfo ToConsentInfo() => new(
        discovered.Manifest.Name,
        discovered.Manifest.Version,
        discovered.Manifest.Author,
        discovered.FolderPath,
        discovered.Sha256);
}
