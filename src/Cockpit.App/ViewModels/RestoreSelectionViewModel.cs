using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Backup;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The choice a restore asks for (#70): the cockpit's own settings, and which of the plugins the archive carries. Read
/// from the manifest, so what is on offer is what is actually in the file — not what this cockpit happens to have
/// installed.
/// </summary>
public sealed partial class RestoreSelectionViewModel : ViewModelBase
{
    public RestoreSelectionViewModel(BackupManifest manifest, IReadOnlyCollection<string> installed)
    {
        MadeOn = $"Made {manifest.CreatedUtc.ToLocalTime():d MMMM yyyy, HH:mm} by AI-Cockpit {manifest.AppVersion}";

        Credentials = manifest.IncludesCredentials
            ? "It carries its own keys and tokens."
            : "It carries none of its keys or tokens — you will have to enter them again.";

        foreach (var (id, version) in manifest.Plugins.OrderBy(plugin => plugin.Key, StringComparer.OrdinalIgnoreCase))
        {
            // Whether this cockpit already has it is the thing the operator wants to know before ticking the box: one
            // is a restore, the other is an install from someone else's machine.
            var detail = installed.Contains(id, StringComparer.OrdinalIgnoreCase)
                ? $"{version} — replaces the one installed here"
                : $"{version} — not installed here";

            Plugins.Add(new RestorePluginViewModel(id, detail, this));
        }
    }

    public string MadeOn { get; }

    public string Credentials { get; }

    public ObservableCollection<RestorePluginViewModel> Plugins { get; } = [];

    public bool HasPlugins => Plugins.Count > 0;

    /// <summary>Nothing ticked means nothing to do, and a Restore button that does nothing is worse than one that is disabled.</summary>
    public bool HasSelection => RestoreSettings || Plugins.Any(plugin => plugin.Selected);

    [ObservableProperty]
    private bool _restoreSettings = true;

    public RestoreOptions ToOptions() => new(
        RestoreSettings,
        Plugins.Where(plugin => plugin.Selected).Select(plugin => plugin.Id).ToList());

    internal void SelectionChanged() => OnPropertyChanged(nameof(HasSelection));

    partial void OnRestoreSettingsChanged(bool value) => SelectionChanged();
}

/// <summary>One plugin the archive carries.</summary>
public sealed partial class RestorePluginViewModel(string id, string detail, RestoreSelectionViewModel owner) : ViewModelBase
{
    public string Id => id;

    public string Detail => detail;

    [ObservableProperty]
    private bool _selected = true;

    partial void OnSelectedChanged(bool value) => owner.SelectionChanged();
}
