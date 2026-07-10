using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One row in a store's plugin catalogue (#14): the advertised display fields plus the install/update
/// state derived by comparing the store's latest version against what is installed. Carries the resolved
/// index URL and the latest version entry so the manager can download and install it.
/// </summary>
public sealed class StorePluginRowViewModel(PluginStoreEntry entry, string indexUrl, string? installedVersion)
{
    public PluginStoreEntry Entry => entry;

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
}
