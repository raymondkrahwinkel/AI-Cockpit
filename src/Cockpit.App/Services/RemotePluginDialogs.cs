using Avalonia.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Plugins;

namespace Cockpit.App.Services;

internal sealed class RemotePluginDialogs(Window owner, SurfaceWindows surfaces) : IPluginManagerDialogs
{
    private Window _Owner => owner.OwnedWindows.LastOrDefault() is { } child
        ? child.OwnedWindows.LastOrDefault() ?? child : owner;

    public async Task ShowPluginStoreDialogAsync(PluginManagerViewModel manager, PluginStoreFilter? initialFilter = null)
    {
        if (surfaces.TryActivateAsync(typeof(PluginStoreDialog)) is { } pending)
        {
            await pending;
            return;
        }
        var dialog = new PluginStoreDialogViewModel(manager, initialFilter);
        await dialog.LoadAsync();
        await surfaces.ShowAsync(typeof(PluginStoreDialog), new PluginStoreDialog { DataContext = dialog }, owner);
    }

    public Task<string?> PickPluginZipAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<bool> ShowPluginConsentAsync(PluginConsentInfo info) =>
        new PluginConsentDialog { DataContext = info }.ShowDialog<bool>(_Owner);

    public Task<bool> ShowPublisherKeyChangedDialogAsync(PluginKeyConsentRequest request) =>
        new PublisherKeyChangedDialog { DataContext = request }.ShowDialog<bool>(_Owner);

    public Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmLabel = "Remove") =>
        new ConfirmationDialog { DataContext = new ConfirmationDialogViewModel(title, message, confirmLabel) }.ShowDialog<bool>(_Owner);
}
