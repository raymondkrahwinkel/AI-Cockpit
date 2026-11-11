using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;

namespace Cockpit.App.Services;

public interface IPluginManagerDialogs
{
    Task ShowPluginStoreDialogAsync(PluginManagerViewModel manager, PluginStoreFilter? initialFilter = null);
    Task<string?> PickPluginZipAsync();
    Task<string?> PickFolderAsync(string title);
    Task<bool> ShowPluginConsentAsync(PluginConsentInfo info);
    Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmLabel = "Remove");
    Task<bool> ShowPublisherKeyChangedDialogAsync(PluginKeyConsentRequest request) => Task.FromResult(false);
}
