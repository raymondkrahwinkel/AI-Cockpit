using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels;

// One row in a store's plugin catalogue (#14): the advertised display fields plus the install/update
// state derived by comparing the store's latest version against what is installed. Carries the store it came
// from (AC-7) and the latest version entry so the manager can download and install it — through the same
// store, with its auth or local path.
//
// Also carries this host's own compatibility numbers (AC-181), so the same "not compatible" verdict the
// install-time gate and the load-time gate would reach is visible here — *before* a click that would
// only fail. `hostAbstractionsMajor`/`hostVersion` default to the running
// cockpit's own values; a caller only ever overrides them in a test.
//
// ObservableObject/partial (AC-553) only for `RemoteLogo` — a vendor CDN logo arrives asynchronously, after
// the row is already on screen, so it is the one property here that needs change notification.
public sealed partial class StorePluginRowViewModel(
    PluginStoreEntry entry,
    PluginStoreConfig store,
    string? installedVersion,
    bool isEnabled = false,
    bool hasSettings = false,
    int hostAbstractionsMajor = AbstractionsContract.Version,
    Version? hostVersion = null) : ObservableObject
{
    private Version EffectiveHostVersion => hostVersion ?? HostVersionInfo.Current;
    public PluginStoreEntry Entry => entry;

    // Whether the installed plugin is currently enabled (false when not installed) — drives the card's enable/disable toggle.
    public bool IsEnabled => isEnabled;

    // Whether the installed plugin registered a settings view — gates the card's ⚙ gear.
    public bool HasSettings => hasSettings;

    // Power icon for the card's enable/disable toggle — colour (via `ToggleBrushKey`) carries the on/off state.
    public MaterialIconKind ToggleGlyph => MaterialIconKind.Power;

    // Theme brush key for the toggle glyph: green when enabled, faint when disabled — resolved in the view by the status-brush converter.
    public string ToggleBrushKey => isEnabled ? "CockpitStatusDoneBrush" : "CockpitTextFaintBrush";

    // Hover text for the enable/disable toggle.
    public string ToggleTooltip => isEnabled ? "Enabled — click to disable (takes effect after restart)" : "Disabled — click to enable (takes effect after restart)";

    // The store this row came from — the manager downloads through it, so a private or local store resolves the same way it was browsed.
    public PluginStoreConfig Store => store;

    public string Id => entry.Id;

    public string Name => entry.Name;

    public string Description => entry.Description ?? "No description provided.";

    public string? Author => entry.Author;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(entry.Author);

    public string LatestVersion => $"v{entry.LatestVersion}";

    public bool IsInstalled => installedVersion is not null;

    // The installed version string (without the "v" prefix), or null when not installed — used to mark the current version in the version picker.
    public string? InstalledVersion => installedVersion;

    public bool UpdateAvailable => installedVersion is not null && PluginVersion.IsNewer(entry.LatestVersion, installedVersion);

    // Offer Install only when it is not already installed.
    public bool CanInstall => !IsInstalled;

    // Offer Update only when installed, the store advertises a newer version, AND this host can actually run
    // it (AC-181) — an update this host does not meet is not offered at all, rather than offered and then
    // failing once downloaded.
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

    // The store version to install — the one matching `PluginStoreEntry.LatestVersion`, else the first listed.
    public PluginStoreVersion? LatestVersionEntry =>
        entry.Versions?.FirstOrDefault(version => version.Version == entry.LatestVersion) ?? entry.Versions?.FirstOrDefault();

    // The store dialog's (#62) sidebar/category-chip label — an uncategorised entry (pre-#62 index, or one that never set it) falls under "Other" rather than showing blank.
    public string Category => string.IsNullOrWhiteSpace(entry.Category) ? OtherCategory : entry.Category;

    // True when the entry declares its own category, as opposed to falling back to `OtherCategory`.
    public bool HasCategory => !string.IsNullOrWhiteSpace(entry.Category);

    // The store dialog's fallback category bucket name for entries without one.
    public const string OtherCategory = "Other";

    // The entry's icon glyph (emoji/unicode character), or null when it did not set one — the card/detail view then falls back to `MonogramLetter`.
    public string? IconGlyphOrNull => string.IsNullOrWhiteSpace(entry.Icon) ? null : entry.Icon;

    // Upper-cased first letter of `Name`, used as the icon fallback when `IconGlyphOrNull` is null.
    public string MonogramLetter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    // Whether `entry.LogoAsset` names a vendor's own CDN rather than a bundled file (AC-553 option A) — decides
    // which download path applies and keeps `LogoAssetUri` from resolving a URL as a bundled file name.
    public bool IsRemoteLogoAsset =>
        Uri.TryCreate(entry.LogoAsset, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // A vendor CDN logo (tier 0), once PluginManagerViewModel's async fetch lands it — null until then, on a
    // failed/oversize/undecodable fetch, or when `IsRemoteLogoAsset` is false. Shown as-is, in the mark's own
    // colours: recolouring a trademarked logo to the app's foreground brush is not this ticket's call to make.
    [ObservableProperty]
    private Bitmap? _remoteLogo;

    partial void OnRemoteLogoChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasRemoteLogo));
        OnPropertyChanged(nameof(ShowsIconGlyph));
        OnPropertyChanged(nameof(ShowsMonogram));
    }

    public bool HasRemoteLogo => RemoteLogo is not null;

    // The bundled `avares://` URI for `entry.LogoAsset` (AC-553), or null when unset, a remote CDN URL, or not
    // actually shipped by this host — falls through to `IconGlyphOrNull`/`MonogramLetter` rather than a broken image.
    public string? LogoAssetUri => IsRemoteLogoAsset ? null : _ResolveLogoAssetUri(entry.LogoAsset);

    public bool HasLogoAsset => LogoAssetUri is not null;

    // Tier 2 (the emoji glyph) only shows once tiers 0 and 1 (a vendor CDN logo, then the bundled vector one) are unavailable.
    public bool ShowsIconGlyph => !HasRemoteLogo && !HasLogoAsset && IconGlyphOrNull is not null;

    // Tier 3, the final fallback: no vendor logo, no bundled logo, no glyph.
    public bool ShowsMonogram => !HasRemoteLogo && !HasLogoAsset && IconGlyphOrNull is null;

    // Which of the theme's five category tints (Theme.axaml) this tile's background wash uses.
    public string CategoryTintBrushKey => PluginCategoryTint.BrushKeyFor(Category);

    // A crafted or malformed asset name — a remote index is attacker-reachable input — must cost the logo, not
    // the row, so this never throws: `Uri.TryCreate` guards a name with characters a URI cannot carry, and
    // `AssetLoader.Exists` is itself wrapped since it is not documented not to throw on every malformed input.
    private static string? _ResolveLogoAssetUri(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
        {
            return null;
        }

        if (!Uri.TryCreate($"avares://Cockpit.App/Assets/PluginLogos/{asset}", UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            return Avalonia.Platform.AssetLoader.Exists(uri) ? uri.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string? Homepage => entry.Homepage;

    public bool HasHomepage => !string.IsNullOrWhiteSpace(entry.Homepage);

    public string? Repository => entry.Repository;

    public bool HasRepository => !string.IsNullOrWhiteSpace(entry.Repository);

    // Whether the store marked this entry for the Discover page's "Featured" rail.
    public bool IsFeatured => entry.Featured;

    // `PluginStoreEntry.Published` parsed as a date, or null when it is missing or not a
    // valid ISO-8601 date — an invalid/absent date must never throw, it just drops out of "Recently
    // added" and sorts last under "Recently updated".
    public DateOnly? PublishedDate => DateOnly.TryParse(entry.Published, out var parsed) ? parsed : null;

    // The store dialog card/detail's primary action button label — "Install", "Update", a disabled "Installed" badge once up to date, or "Incompatible" when nothing is installed and this host cannot install it either.
    public string PrimaryActionLabel => IsIncompatible ? "Incompatible" : !IsInstalled ? "Install" : CanUpdate ? "Update" : "Installed";

    // Whether the primary action button does anything — false once installed and up to date (a disabled badge instead) or when this host cannot take the action at all (AC-181): the store stays a catalogue that shows everything, but a button that would only fail here is disabled rather than clickable.
    public bool CanTakePrimaryAction => !IsIncompatible && (CanInstall || CanUpdate);

    // At-a-glance install-state chip for the catalogue row: "Incompatible" (AC-181, only when nothing is installed — see `IsIncompatible`) takes priority, then "Update"/"Installed", or null when not installed and compatible (nothing shown).
    public string? StateBadgeText => IsIncompatible ? "Incompatible" : !IsInstalled ? null : CanUpdate ? "Update" : "Installed";

    // Whether to show the `StateBadgeText` chip — once installed, or whenever this host cannot install the plugin at all.
    public bool HasStateBadge => IsIncompatible || IsInstalled;

    // Theme brush key for the state chip: red when this host cannot install it at all, amber when a (compatible) update is available, green once up to date — resolved in the view by the status-brush converter.
    public string StateBadgeBrushKey => IsIncompatible ? "CockpitStatusErrorBrush" : CanUpdate ? "CockpitStatusWaitingBrush" : "CockpitStatusDoneBrush";

    // Hover text for the state chip — the reason this host cannot install it fresh, or null (an already-installed plugin's own status line, not this tooltip, explains an update it merely cannot take — see `StatusText`).
    public string? StateBadgeTooltip => IsIncompatible ? IncompatibilityReason : null;

    // True when nothing is installed yet and this host cannot install `LatestVersionEntry` either
    // (AC-181) — a contract-major mismatch, or a `minHostVersion` this host does not meet (see
    // `PluginLoadPolicy.MeetsMinHostVersion`, the same gate the install- and load-time checks apply,
    // so this can never disagree with what an actual install attempt would do). Deliberately never true once the
    // plugin is already installed and running: only the newer version on offer may be out of reach, which is not
    // the same claim as "this plugin does not work here" — `CanUpdate`/`StatusText`
    // carry that distinction instead of mislabelling a working plugin. A version the catalogue declares nothing
    // about is never flagged incompatible over it — an absent field means "nothing declared", not "unsupported".
    public bool IsIncompatible =>
        !IsInstalled && LatestVersionEntry is { } version && !PluginCompatibility.IsCompatible(version, hostAbstractionsMajor, EffectiveHostVersion);

    // Why this host cannot run `LatestVersionEntry`, or null when it can (or there is no version to judge).
    public string? IncompatibilityReason =>
        LatestVersionEntry is { } version ? PluginCompatibility.IncompatibilityReason(version, hostAbstractionsMajor, EffectiveHostVersion) : null;
}
