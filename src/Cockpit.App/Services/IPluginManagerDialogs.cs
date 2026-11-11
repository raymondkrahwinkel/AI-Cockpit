using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;

namespace Cockpit.App.Services;

public interface IPluginManagerDialogs : ICommonDialogs
{
    Task ShowPluginStoreDialogAsync(PluginManagerViewModel manager, PluginStoreFilter? initialFilter = null);
    Task<string?> PickPluginZipAsync();
    Task<bool> ShowPluginConsentAsync(PluginConsentInfo info);
    Task<bool> ShowPublisherKeyChangedDialogAsync(PluginKeyConsentRequest request) => Task.FromResult(false);
}
