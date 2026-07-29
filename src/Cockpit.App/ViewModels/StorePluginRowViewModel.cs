using Material.Icons;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One row in a store's plugin catalogue (#14): the advertised display fields plus the install/update
/// state derived by comparing the store's latest version against what is installed. Carries the store it came
/// from (AC-7) and the latest version entry so the manager can download and install it — through the same
/// store, with its auth or local path.
/// <para>
/// Also carries this host's own compatibility numbers (AC-181), so the same "not compatible" verdict the
/// install-time gate and the load-time gate would reach is visible here — <em>before</em> a click that would
/// only fail. <paramref name="hostAbstractionsMajor"/>/<paramref name="hostVersion"/> default to the running
/// cockpit's own values; a caller only ever overrides them in a test.
/// </para>
/// </summary>
public sealed class StorePluginRowViewModel(
    PluginStoreEntry entry,
    PluginStoreConfig store,
    string? installedVersion,
    bool isEnabled = false,
    bool hasSettings = false,
    int hostAbstractionsMajor = AbstractionsContract.Version,
    Version? hostVersion = null)
{
    private Version EffectiveHostVersion => hostVersion ?? HostVersionInfo.Current;
    public PluginStoreEntry Entry => entry;

    /// <summary>Whether the installed plugin is currently enabled (false when not installed) — drives the card's enable/disable toggle.</summary>
    public bool IsEnabled => isEnabled;

    /// <summary>Whether the installed plugin registered a settings view — gates the card's ⚙ gear.</summary>
    public bool HasSettings => hasSettings;

    /// <summary>Power icon for the card's enable/disable toggle — colour (via <see cref="ToggleBrushKey"/>) carries the on/off state.</summary>
    public MaterialIconKind ToggleGlyph => MaterialIconKind.Power;

    /// <summary>Theme brush key for the toggle glyph: green when enabled, faint when disabled — resolved in the view by the status-brush converter.</summary>
    public string ToggleBrushKey => isEnabled ? "CockpitStatusDoneBrush" : "CockpitTextFaintBrush";

    /// <summary>Hover text for the enable/disable toggle.</summary>
    public string ToggleTooltip => isEnabled ? "Enabled — click to disable (takes effect after restart)" : "Disabled — click to enable (takes effect after restart)";

    /// <summary>The store this row came from — the manager downloads through it, so a private or local store resolves the same way it was browsed.</summary>
    public PluginStoreConfig Store => store;

    public string Id => entry.Id;

    public string Name => entry.Name;

    public string Description => entry.Description ?? "No description provided.";

    public string? Author => entry.Author;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(entry.Author);

    public string LatestVersion => $"v{entry.LatestVersion}";

    public bool IsInstalled => installedVersion is not null;

    /// <summary>The installed version string (without the "v" prefix), or null when not installed — used to mark the current version in the version picker.</summary>
    public string? InstalledVersion => installedVersion;

    public bool UpdateAvailable => installedVersion is not null && PluginVersion.IsNewer(entry.LatestVersion, installedVersion);

    /// <summary>Offer Install only when it is not already installed.</summary>
    public bool CanInstall => !IsInstalled;

    /// <summary>
    /// Offer Update only when installed, the store advertises a newer version, AND this host can actually run
    /// it (AC-181) — an update this host does not meet is not offered at all, rather than offered and then
    /// failing once downloaded.
    /// </summary>
    public bool CanUpdate => UpdateAvailable && IncompatibilityReason is null;

    public string StatusText
    {
        get
        {
            if (installedVersion is null)
            {
                return IncompatibilityReason ?? "Available";
            }

            if (!UpdateAvailable)
            {
                return $"Installed v{installedVersion} — up to date";
            }

            // Already installed and running fine — the plugin itself is not incompatible, only the newer
            // version on offer is, so this stays a plain status line rather than the red "Incompatible" state.
            return IncompatibilityReason is { } reason
                ? $"Installed v{installedVersion} — v{entry.LatestVersion} available: {reason}"
                : $"Installed v{installedVersion} — update to v{entry.LatestVersion}";
        }
    }

    /// <summary>The store version to install — the one matching <see cref="PluginStoreEntry.LatestVersion"/>, else the first listed.</summary>
    public PluginStoreVersion? LatestVersionEntry =>
        entry.Versions?.FirstOrDefault(version => version.Version == entry.LatestVersion) ?? entry.Versions?.FirstOrDefault();

    /// <summary>The store dialog's (#62) sidebar/category-chip label — an uncategorised entry (pre-#62 index, or one that never set it) falls under "Other" rather than showing blank.</summary>
    public string Category => string.IsNullOrWhiteSpace(entry.Category) ? OtherCategory : entry.Category;

    /// <summary>True when the entry declares its own category, as opposed to falling back to <see cref="OtherCategory"/>.</summary>
    public bool HasCategory => !string.IsNullOrWhiteSpace(entry.Category);

    /// <summary>The store dialog's fallback category bucket name for entries without one.</summary>
    public const string OtherCategory = "Other";

    /// <summary>The entry's icon glyph (emoji/unicode character), or null when it did not set one — the card/detail view then falls back to <see cref="MonogramLetter"/>.</summary>
    public string? IconGlyphOrNull => string.IsNullOrWhiteSpace(entry.Icon) ? null : entry.Icon;

    /// <summary>Upper-cased first letter of <see cref="Name"/>, used as the icon fallback when <see cref="IconGlyphOrNull"/> is null.</summary>
    public string MonogramLetter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public string? Homepage => entry.Homepage;

    public bool HasHomepage => !string.IsNullOrWhiteSpace(entry.Homepage);

    public string? Repository => entry.Repository;

    public bool HasRepository => !string.IsNullOrWhiteSpace(entry.Repository);

    /// <summary>Whether the store marked this entry for the Discover page's "Featured" rail.</summary>
    public bool IsFeatured => entry.Featured;

    /// <summary>
    /// <see cref="PluginStoreEntry.Published"/> parsed as a date, or null when it is missing or not a
    /// valid ISO-8601 date — an invalid/absent date must never throw, it just drops out of "Recently
    /// added" and sorts last under "Recently updated".
    /// </summary>
    public DateOnly? PublishedDate => DateOnly.TryParse(entry.Published, out var parsed) ? parsed : null;

    /// <summary>The store dialog card/detail's primary action button label — "Install", "Update", a disabled "Installed" badge once up to date, or "Incompatible" when nothing is installed and this host cannot install it either.</summary>
    public string PrimaryActionLabel => IsIncompatible ? "Incompatible" : !IsInstalled ? "Install" : CanUpdate ? "Update" : "Installed";

    /// <summary>Whether the primary action button does anything — false once installed and up to date (a disabled badge instead) or when this host cannot take the action at all (AC-181): the store stays a catalogue that shows everything, but a button that would only fail here is disabled rather than clickable.</summary>
    public bool CanTakePrimaryAction => !IsIncompatible && (CanInstall || CanUpdate);

    /// <summary>At-a-glance install-state chip for the catalogue row: "Incompatible" (AC-181, only when nothing is installed — see <see cref="IsIncompatible"/>) takes priority, then "Update"/"Installed", or null when not installed and compatible (nothing shown).</summary>
    public string? StateBadgeText => IsIncompatible ? "Incompatible" : !IsInstalled ? null : CanUpdate ? "Update" : "Installed";

    /// <summary>Whether to show the <see cref="StateBadgeText"/> chip — once installed, or whenever this host cannot install the plugin at all.</summary>
    public bool HasStateBadge => IsIncompatible || IsInstalled;

    /// <summary>Theme brush key for the state chip: red when this host cannot install it at all, amber when a (compatible) update is available, green once up to date — resolved in the view by the status-brush converter.</summary>
    public string StateBadgeBrushKey => IsIncompatible ? "CockpitStatusErrorBrush" : CanUpdate ? "CockpitStatusWaitingBrush" : "CockpitStatusDoneBrush";

    /// <summary>Hover text for the state chip — the reason this host cannot install it fresh, or null (an already-installed plugin's own status line, not this tooltip, explains an update it merely cannot take — see <see cref="StatusText"/>).</summary>
    public string? StateBadgeTooltip => IsIncompatible ? IncompatibilityReason : null;

    /// <summary>
    /// True when nothing is installed yet and this host cannot install <see cref="LatestVersionEntry"/> either
    /// (AC-181) — a contract-major mismatch, or a <c>minHostVersion</c> this host does not meet (see
    /// <see cref="PluginLoadPolicy.MeetsMinHostVersion"/>, the same gate the install- and load-time checks apply,
    /// so this can never disagree with what an actual install attempt would do). Deliberately never true once the
    /// plugin is already installed and running: only the newer version on offer may be out of reach, which is not
    /// the same claim as "this plugin does not work here" — <see cref="CanUpdate"/>/<see cref="StatusText"/>
    /// carry that distinction instead of mislabelling a working plugin. A version the catalogue declares nothing
    /// about is never flagged incompatible over it — an absent field means "nothing declared", not "unsupported".
    /// </summary>
    public bool IsIncompatible => !IsInstalled && LatestVersionEntry is { } version && !_IsCompatible(version);

    /// <summary>Why this host cannot run <see cref="LatestVersionEntry"/>, or null when it can (or there is no version to judge).</summary>
    public string? IncompatibilityReason
    {
        get
        {
            if (LatestVersionEntry is not { } version || _IsCompatible(version))
            {
                return null;
            }

            return version.AbstractionsVersion is { } abstractionsVersion && abstractionsVersion != hostAbstractionsMajor
                ? $"Built for plugin contract version {abstractionsVersion}, this cockpit provides {hostAbstractionsMajor}"
                : $"Requires {CockpitProduct.DisplayName} {version.MinHostVersion} or later";
        }
    }

    private bool _IsCompatible(PluginStoreVersion version) =>
        (version.AbstractionsVersion is not { } abstractionsVersion || abstractionsVersion == hostAbstractionsMajor)
        && PluginLoadPolicy.MeetsMinHostVersion(version.MinHostVersion, EffectiveHostVersion);
}
