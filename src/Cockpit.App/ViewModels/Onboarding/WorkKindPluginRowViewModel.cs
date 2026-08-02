using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels.Onboarding;

/// <summary>
/// One plugin on the work-kind step (AC-511), carrying per row what the per-plugin consent dialog carries per
/// dialog: identity, where the code comes from, the checksum that gets verified, and what it may do.
/// </summary>
/// <remarks>
/// Two fields read differently here than in <c>PluginConsentDialog</c> because nothing has been downloaded yet:
/// <see cref="From"/> is the store and zip path rather than an installed folder, and <see cref="Checksum"/> is the
/// checksum the store publishes rather than the installed assembly's hash. Both are what is actually verified at
/// this point in the walk.
/// </remarks>
public sealed partial class WorkKindPluginRowViewModel : ObservableObject
{
    public WorkKindPluginRowViewModel(PluginStoreEntry entry, PluginStoreConfig store, PluginStoreVersion version)
    {
        Name = entry.Name;
        Version = version.Version;
        Author = entry.Author;
        WorkKind = entry.WorkKind;
        From = $"{store.Location} → {version.Path}";
        Checksum = version.Sha256 ?? "This store publishes no checksum for this version.";
        Request = new PluginProvisionRequest(entry.Id, entry.Name, store, version);
    }

    /// <summary>Design-time/preview row: the same fields, without a store to install from.</summary>
    internal WorkKindPluginRowViewModel(string name, string version, string author, string from, string checksum, bool isSelected)
    {
        Name = name;
        Version = version;
        Author = author;
        From = from;
        Checksum = checksum;
        IsSelected = isSelected;
    }

    public string Name { get; }

    public string Version { get; }

    public string? Author { get; }

    /// <summary>The store this plugin comes from and the zip within it — the consent dialog's "Location", before there is a folder.</summary>
    public string From { get; }

    /// <summary>The published SHA-256 the download is verified against and the install pins, or why there is none.</summary>
    public string Checksum { get; }

    /// <summary>What enabling this plugin grants. Same for every plugin until capability grants exist (AC-107).</summary>
    public string May => PluginConsentTerms.PermissionSummary;

    /// <summary>The work kind the store index advertises for this plugin, or null when the index predates the field.</summary>
    public string? WorkKind { get; }

    /// <summary>Ticked by the chosen work kind, and by hand either way — nothing installs until the batch is confirmed.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What to hand the provisioning service for this row; absent on a preview row, which installs nothing.</summary>
    internal PluginProvisionRequest? Request { get; }
}
