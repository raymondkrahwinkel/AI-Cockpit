using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One row in a store's plugin catalogue (#14): the advertised display fields plus the install/update
/// state derived by comparing the store's latest version against what is installed. Carries the resolved
/// index URL and the latest version entry so the manager can download and install it.
/// </summary>
public sealed class StorePluginRowViewModel(PluginStoreEntry entry, string indexUrl, string? installedVersion, bool isEnabled = false, bool hasSettings = false)
{
    public PluginStoreEntry Entry => entry;

    /// <summary>Whether the installed plugin is currently enabled (false when not installed) — drives the card's enable/disable toggle.</summary>
    public bool IsEnabled => isEnabled;

    /// <summary>Whether the installed plugin registered a settings view — gates the card's ⚙ gear.</summary>
    public bool HasSettings => hasSettings;

    /// <summary>Power glyph for the card's enable/disable toggle: filled when enabled, hollow when disabled.</summary>
    public string ToggleGlyph => isEnabled ? "⏼" : "⭘";

    /// <summary>Hover text for the enable/disable toggle.</summary>
    public string ToggleTooltip => isEnabled ? "Disable this plugin (takes effect after restart)" : "Enable this plugin (takes effect after restart)";

    public string IndexUrl => indexUrl;

    public string Id => entry.Id;

    public string Name => entry.Name;

    public string Description => entry.Description ?? "No description provided.";

    public string? Author => entry.Author;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(entry.Author);

    public string LatestVersion => $"v{entry.LatestVersion}";

    public bool IsInstalled => installedVersion is not null;

    public bool UpdateAvailable => installedVersion is not null && PluginVersion.IsNewer(entry.LatestVersion, installedVersion);

    /// <summary>Offer Install only when it is not already installed.</summary>
    public bool CanInstall => !IsInstalled;

    /// <summary>Offer Update only when installed and the store advertises a newer version.</summary>
    public bool CanUpdate => UpdateAvailable;

    public string StatusText => installedVersion is null
        ? "Available"
        : UpdateAvailable
            ? $"Installed v{installedVersion} — update to v{entry.LatestVersion}"
            : $"Installed v{installedVersion} — up to date";

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

    /// <summary>The store dialog card/detail's primary action button label — "Install", "Update", or a disabled "Installed" badge once up to date.</summary>
    public string PrimaryActionLabel => !IsInstalled ? "Install" : UpdateAvailable ? "Update" : "Installed ✓";

    /// <summary>Whether the primary action button does anything — false once installed and up to date, when it becomes a disabled badge instead.</summary>
    public bool CanTakePrimaryAction => CanInstall || CanUpdate;
}
