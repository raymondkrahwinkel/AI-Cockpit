using Cockpit.App.Plugins;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One row in the plugin manager (#14): the display fields plus the action affordances derived from the
/// plugin's <see cref="PluginLoadDecision"/>. The manager owns the enable/disable/remove commands and
/// takes the row as their parameter, so the row itself stays a passive projection of a discovered plugin.
/// </summary>
public sealed class PluginRowViewModel(DiscoveredPlugin discovered, bool hasSettings = false, IReadOnlyList<PluginFailure>? failures = null, bool hiddenInMenu = false)
{
    private readonly IReadOnlyList<PluginFailure> _failures = failures ?? [];

    // Three independent facts can live in the same folder's history (#184): whether it ever became operative,
    // whether it is flagged as built for a newer SDK, and whether a contribution it registered later failed. A
    // single "the failure" would collapse them — an initialize failure and a later mcp-server one are not the
    // same fact, and neither should hide the other.
    private PluginFailure? _ActivationFailure => _failures.LastOrDefault(failure => PluginDiagnostics.ActivationPhases.Contains(failure.Phase));

    private PluginFailure? _CompatibilityWarning => _failures.LastOrDefault(failure => failure.Phase == "compatibility");

    private PluginFailure? _McpContributionFailure => _failures.LastOrDefault(failure => failure.Phase == "mcp-server");

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

    /// <summary>True when this plugin never became operative (load/configure/initialize), or is flagged for a compatibility concern.</summary>
    public bool HasFailure => _ActivationFailure is not null || _CompatibilityWarning is not null;

    /// <summary>The load/init failure or compatibility warning for this plugin, if any (#14) — a contribution failing later (#184) is a separate fact, see <see cref="McpContributionFailureText"/>.</summary>
    public string FailureText => _ActivationFailure switch
    {
        { } activation => $"Failed to load: {activation.Error}",
        null => _CompatibilityWarning?.Error ?? string.Empty,
    };

    /// <summary>True when this plugin loaded but a contribution it registered afterwards failed (#184) — e.g. its MCP server upsert. Independent of <see cref="HasFailure"/>: the plugin is still running.</summary>
    public bool HasMcpContributionFailure => _McpContributionFailure is not null;

    public string McpContributionFailureText => _McpContributionFailure is { } mcp
        ? $"Its MCP server contribution failed: {mcp.Error}"
        : string.Empty;

    public string FolderId => discovered.FolderId;

    public string DisplayName => discovered.Manifest.Name;

    public string Version => $"v{discovered.Manifest.Version}";

    public string? Author => discovered.Manifest.Author;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(discovered.Manifest.Author);

    public string Description => discovered.Manifest.Description ?? "No description provided.";

    public string StatusText => discovered.Decision switch
    {
        // Load alone is a discovery-time decision (#184) — a plugin that threw while loading, configuring or
        // initializing never became operative even though it was decided to load, and reporting it as enabled
        // would say the opposite of what happened.
        PluginLoadDecision.Load when _ActivationFailure is not null => "Failed to load — see below",
        PluginLoadDecision.Load => "Enabled — active this session",
        PluginLoadDecision.Disabled => "Disabled",
        PluginLoadDecision.NeedsConsent => "Needs your consent",
        PluginLoadDecision.AbstractionsMajorMismatch => "Incompatible — built for another contract version",
        // Named in full here, unlike the rest of the running text: the sentence carries two version numbers'
        // worth of ambiguity otherwise — the reader cannot tell whether it is the plugin's version or the host's.
        PluginLoadDecision.HostTooOld => $"Needs {CockpitProduct.DisplayName} {discovered.Manifest.MinHostVersion} or later",
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
