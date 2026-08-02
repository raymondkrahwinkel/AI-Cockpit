using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One plugin, and whether it goes into the backup (#70) — its binaries and everything it stored, which travel together or not at all.
public sealed partial class BackupPluginViewModel(string id, string name) : ViewModelBase
{
    public string Id => id;

    public string Name => name;

    [ObservableProperty]
    private bool _selected = true;
}
