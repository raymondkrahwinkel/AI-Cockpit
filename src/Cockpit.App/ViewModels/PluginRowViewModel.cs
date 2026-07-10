using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One row in the plugin manager (#14): the display fields plus the action affordances derived from the
/// plugin's <see cref="PluginLoadDecision"/>. The manager owns the enable/disable/remove commands and
/// takes the row as their parameter, so the row itself stays a passive projection of a discovered plugin.
/// </summary>
public sealed class PluginRowViewModel(DiscoveredPlugin discovered, bool hasSettings = false)
{
    public DiscoveredPlugin Discovered => discovered;

    /// <summary>True when the loaded plugin registered a settings view (#14) — the manager shows a gear to open it.</summary>
    public bool HasSettings => hasSettings;

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
        _ => string.Empty,
    };

    /// <summary>The plugin can be enabled (it is disabled or awaiting consent) — enabling always shows the consent dialog.</summary>
    public bool CanEnable => discovered.Decision is PluginLoadDecision.Disabled or PluginLoadDecision.NeedsConsent;

    /// <summary>The plugin is enabled and consented, so the only state change offered is to disable it.</summary>
    public bool CanDisable => discovered.Decision is PluginLoadDecision.Load;

    /// <summary>A version-incompatible plugin cannot be enabled at all — the manager shows why instead of an Enable button.</summary>
    public bool IsIncompatible => discovered.Decision is PluginLoadDecision.AbstractionsMajorMismatch;

    public string EnableLabel => discovered.Decision is PluginLoadDecision.NeedsConsent ? "Review & enable" : "Enable";

    public PluginConsentInfo ToConsentInfo() => new(
        discovered.Manifest.Name,
        discovered.Manifest.Version,
        discovered.Manifest.Author,
        discovered.FolderPath,
        discovered.Sha256);
}
