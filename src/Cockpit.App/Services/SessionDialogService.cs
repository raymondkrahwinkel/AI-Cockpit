using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Infrastructure.Claude;

namespace Cockpit.App.Services;

/// <summary>
/// Hosts the modal dialogs over the main window. Constructs each dialog's view model with the profile
/// store/login checker it injects, so the dialogs get their data without a service locator, then shows
/// it with <c>ShowDialog</c> and relays the typed result back to the caller.
/// </summary>
public sealed class SessionDialogService : ISessionDialogService, ISingletonService
{
    private readonly IClaudeProfileStore _profileStore;
    private readonly IClaudeProfileLoginChecker _loginChecker;
    private readonly IModelCatalog _modelCatalog;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly IPluginProviderRegistry _pluginProviderRegistry;
    private readonly IWorkingPathHistoryStore _workingPathStore;

    public SessionDialogService(
        IClaudeProfileStore profileStore,
        IClaudeProfileLoginChecker loginChecker,
        IModelCatalog modelCatalog,
        IMcpServerStore mcpServerStore,
        IPluginProviderRegistry pluginProviderRegistry,
        IWorkingPathHistoryStore workingPathStore)
    {
        _profileStore = profileStore;
        _loginChecker = loginChecker;
        _modelCatalog = modelCatalog;
        _mcpServerStore = mcpServerStore;
        _pluginProviderRegistry = pluginProviderRegistry;
        _workingPathStore = workingPathStore;
    }

    public async Task<NewSessionResult?> ShowNewSessionDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var viewModel = new NewSessionDialogViewModel(_profileStore, _loginChecker, _mcpServerStore, _workingPathStore);
        await viewModel.LoadAsync();

        var dialog = new NewSessionDialog { DataContext = viewModel };

        // Managing profiles from within the New-session dialog opens the Manage dialog over it, then
        // reloads the picker so any added/edited/removed profile (and its defaults) shows immediately.
        // async void via the Action event: guard it so a dialog/store failure can't tear the process
        // down — worst case the picker just doesn't refresh.
        viewModel.ManageProfilesRequested += async () =>
        {
            try
            {
                await ShowManageProfilesAsync(dialog);
                await viewModel.LoadAsync();
            }
            catch
            {
                // Managing profiles is best-effort from here; a failure must not crash the app.
            }
        };

        return await dialog.ShowDialog<NewSessionResult?>(owner);
    }

    public async Task ShowManageProfilesDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            await ShowManageProfilesAsync(owner);
        }
    }

    private async Task ShowManageProfilesAsync(Window owner)
    {
        var viewModel = new ManageProfilesDialogViewModel(_profileStore, _loginChecker, _modelCatalog, _pluginProviderRegistry);
        await viewModel.LoadAsync();

        var dialog = new ManageProfilesDialog { DataContext = viewModel };
        await dialog.ShowDialog(owner);
    }

    public async Task ShowMcpServersDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        var viewModel = new McpServersViewModel(_mcpServerStore);
        await viewModel.LoadAsync();

        var dialog = new McpServersDialog { DataContext = viewModel };
        viewModel.CloseRequested += dialog.Close;
        await dialog.ShowDialog(owner);
    }

    public async Task ShowPluginStoreDialogAsync(PluginManagerViewModel manager, PluginStoreFilter? initialFilter = null)
    {
        if (_ActiveOwnerWindow() is not { } owner)
        {
            return;
        }

        var viewModel = new PluginStoreDialogViewModel(manager, initialFilter);
        var dialog = new PluginStoreDialog { DataContext = viewModel };
        await viewModel.LoadAsync();
        await dialog.ShowDialog(owner);
    }

    // Most dialogs are only ever shown over the main window, so they hardcode it as the owner. The store
    // dialog can itself be a step below another modal (Options → Store), so it — and anything the store
    // dialog opens in turn, like plugin consent — needs the topmost active window instead, or it centers
    // behind the dialog stack rather than over it (#62 design-doc caveat).
    private static Window? _ActiveOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main } lifetime)
        {
            return null;
        }

        return lifetime.Windows.LastOrDefault(window => window.IsActive) ?? main;
    }

    public async Task ShowOptionsDialogAsync(CockpitViewModel viewModel, bool selectPluginsTab = false)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        var dialog = new OptionsDialog { DataContext = viewModel };
        if (selectPluginsTab)
        {
            dialog.SelectPluginsTab();
        }

        await dialog.ShowDialog(owner);
    }

    public async Task<string?> PickPluginZipAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install plugin from zip",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Plugin package (*.zip)") { Patterns = ["*.zip"] }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ShowPluginConsentAsync(PluginConsentInfo info)
    {
        // Uses the active window, not always MainWindow: an install/update triggered from the plugin store
        // dialog (itself opened over Options) must show consent over that dialog stack, not behind it.
        if (_ActiveOwnerWindow() is not { } owner)
        {
            return false;
        }

        var dialog = new PluginConsentDialog { DataContext = info };
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task ShowAboutDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly());
        var dialog = new AboutDialog { DataContext = info };
        await dialog.ShowDialog(owner);
    }
}
