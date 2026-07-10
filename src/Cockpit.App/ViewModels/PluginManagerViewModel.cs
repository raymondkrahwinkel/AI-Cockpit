using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The "Plugins" Options tab (#14, contribution point 4): lists every installed plugin with its enable/
/// consent state and offers install-from-zip, enable (with first-load consent), disable and remove. It is
/// a config editor over discovery + the registration store — enabling/disabling/removing takes effect on
/// the next restart (a non-collectible plugin cannot be loaded or unloaded live), which the tab states
/// plainly rather than pretending otherwise.
/// </summary>
public partial class PluginManagerViewModel : ViewModelBase
{
    private readonly IPluginRegistrationStore? _registrationStore;
    private readonly IPluginInstaller? _installer;
    private readonly PluginBootstrap? _bootstrap;
    private readonly ISessionDialogService? _dialogService;

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasPlugins;

    /// <summary>Design-time constructor for the previewer.</summary>
    public PluginManagerViewModel()
    {
    }

    public PluginManagerViewModel(
        IPluginRegistrationStore registrationStore,
        IPluginInstaller installer,
        PluginBootstrap bootstrap,
        ISessionDialogService dialogService)
    {
        _registrationStore = registrationStore;
        _installer = installer;
        _bootstrap = bootstrap;
        _dialogService = dialogService;
    }

    /// <summary>Rediscovers the installed plugins and rebuilds the rows; called when the Options dialog opens and after every change.</summary>
    public async Task LoadAsync()
    {
        if (_bootstrap is null)
        {
            return;
        }

        var discovered = await _bootstrap.DiscoverAsync(AbstractionsContract.Version);
        Plugins.Clear();
        foreach (var plugin in discovered)
        {
            Plugins.Add(new PluginRowViewModel(plugin));
        }

        HasPlugins = Plugins.Count > 0;
    }

    [RelayCommand]
    private async Task InstallFromZipAsync()
    {
        if (_dialogService is null || _installer is null)
        {
            return;
        }

        var zipPath = await _dialogService.PickPluginZipAsync();
        if (zipPath is null)
        {
            return;
        }

        var result = await _installer.InstallFromZipAsync(zipPath, AbstractionsContract.Version);
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error ?? "Install failed.";
            return;
        }

        await LoadAsync();

        // A freshly installed plugin lands as "needs consent"; walk the operator straight into that step.
        var installed = Plugins.FirstOrDefault(row => row.FolderId == result.FolderId);
        if (installed is not null && installed.CanEnable)
        {
            await EnablePluginAsync(installed);
        }
        else
        {
            StatusMessage = "Plugin installed. Restart the cockpit to activate it.";
        }
    }

    [RelayCommand]
    private async Task EnablePluginAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null || _dialogService is null)
        {
            return;
        }

        // Enabling always requires consent to the current bytes: the operator sees what they are trusting
        // and the shown SHA-256 is what gets pinned.
        var consented = await _dialogService.ShowPluginConsentAsync(row.ToConsentInfo());
        if (!consented)
        {
            return;
        }

        await _registrationStore.SaveAsync(row.FolderId, new PluginRegistration(Enabled: true, PinnedSha256: row.Discovered.Sha256));
        await LoadAsync();
        StatusMessage = $"'{row.DisplayName}' enabled. Restart the cockpit to load it.";
    }

    [RelayCommand]
    private async Task DisablePluginAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null)
        {
            return;
        }

        await _registrationStore.SaveAsync(row.FolderId, new PluginRegistration(Enabled: false, PinnedSha256: row.Discovered.Sha256));
        await LoadAsync();
        StatusMessage = $"'{row.DisplayName}' disabled. Restart the cockpit to unload it.";
    }

    [RelayCommand]
    private async Task RemovePluginAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null || _installer is null)
        {
            return;
        }

        await _installer.MarkForRemovalAsync(row.FolderId);
        await _registrationStore.RemoveAsync(row.FolderId);
        await LoadAsync();
        StatusMessage = $"'{row.DisplayName}' will be removed on the next restart.";
    }
}
