using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Cockpit.App.Docking;
using Cockpit.App.Composition;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Plugins;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Plugins;

// AC-2283: the ordinary window's UI registries, without a backend container or any hosted services.
internal sealed class RemoteWindowUi : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IServiceProvider _windowServices;
    private readonly Window _owner;
    private readonly SurfaceWindows _surfaces;
    private readonly ILoggerFactory _loggers;
    private readonly List<IDisposable> _lifetimes = [];
    private readonly Dictionary<string, (string Sha256, PluginRuntime Runtime)> _pluginRuntimes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unavailable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changing = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly IPluginDialogHost _dialogs;
    private CockpitViewModel? _cockpit;
    private Action? _stopWatchingServer;
    private bool _disposed;

    internal RemoteWindowUi(Window owner, SurfaceWindows surfaces, ILoggerFactory loggers)
    {
        _owner = owner;
        _surfaces = surfaces;
        _loggers = loggers;
        _dialogs = new PluginDialogHost(surfaces, owner);
        var services = new ServiceCollection();
        services.AddSingleton(loggers);
        services.AddLogging();
        services.AddSingleton<IWorkspaceTypeRegistry, WorkspaceTypeRegistry>();
        services.AddSingleton<IWidgetRegistry, WidgetRegistry>();
        services.AddSingleton<IDockPanelRegistry, DockPanelRegistry>();
        services.AddSingleton<ICompanionToolRegistry, CompanionToolRegistry>();
        services.AddSingleton<IConversationPickerRegistry, ConversationPickerRegistry>();
        services.AddSingleton<IPluginProviderConfigViews, PluginProviderConfigViews>();
        _services = services.BuildServiceProvider();
        _windowServices = new WindowServices(_services, () => _cockpit);
    }

    internal IWorkspaceTypeRegistry WorkspaceTypes => _services.GetRequiredService<IWorkspaceTypeRegistry>();
    internal IWidgetRegistry Widgets => _services.GetRequiredService<IWidgetRegistry>();
    internal IDockPanelRegistry DockPanels => _services.GetRequiredService<IDockPanelRegistry>();
    internal IPluginProviderConfigViews ProviderConfigViews => _services.GetRequiredService<IPluginProviderConfigViews>();

    internal async Task ShowAssistantProfileAsync(AssistantProfileDialogViewModel dialog)
    {
        if (_surfaces.TryActivateAsync(typeof(AssistantProfileDialog)) is { } pending)
        {
            await pending;
            return;
        }
        await _surfaces.ShowAsync(typeof(AssistantProfileDialog), new AssistantProfileDialog { DataContext = dialog }, _owner);
    }

    internal Task ShowWorktreesAsync(WorktreesViewModel worktrees) => RemoteFileUi.ShowWorktreesAsync(worktrees, _owner, _surfaces);
    internal Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => new WindowActions(_owner).ConfirmAsync(title, message, confirmLabel);

    internal async Task InitializeAsync(CockpitViewModel cockpit, IRemoteServers servers, string serverName, string localRoot)
    {
        _cockpit = cockpit;
        try
        {
            await servers.ReloadAsync();
            var server = servers.Servers.FirstOrDefault(target => target.Name == serverName)
                ?? throw new InvalidOperationException("The server is not connected.");
            if (_disposed)
            {
                return;
            }
            var sessions = new PluginSessionObserver(cockpit, server: server);
            var actions = new WindowActions(_owner);
            await cockpit.Worktrees.UseRemoteAsync(RemoteProjectFiles.For(server), actions.ConfirmAsync);
            var fileRuntime = new PluginRuntime("server-project-files", _loggers.CreateLogger<RemoteWindowUi>());
            var fileHost = new CockpitUiHost("server-project-files", "Files",
                new RemoteUiBackend(cockpit, sessions, new PluginStorage(new Dictionary<string, string>(), _ => { })),
                _windowServices, cockpit, _dialogs, actions, sessions, [], fileRuntime,
                new RemotePluginUiChannel((IPluginChannelRelay)server.Plugins, "server-project-files"));
            fileRuntime.Add(RemoteFileUi.Attach(fileHost, server));
            _lifetimes.Add(fileRuntime);
            PluginManagerViewModel? manager = null;
            var restarting = false;
            var disconnected = false;
            DateTimeOffset? startedAt = null;
            manager = new PluginManagerViewModel(server.Plugins, new RemotePluginDialogs(_owner, _surfaces), [], serverName,
                restartService: server.Plugins is IServerRestart restart ? new ServerRestartService(async () =>
                {
                    if (manager is null || restarting)
                    {
                        return;
                    }
                    restarting = true;
                    disconnected = false;
                    startedAt = server.State.Key?.StartedAt;
                    manager.IsBusy = true;
                    manager.StatusIsError = false;
                    manager.StatusMessage = $"Restarting {serverName}. Waiting for it to reconnect…";
                    try
                    {
                        await restart.RestartAsync();
                    }
                    catch (Exception exception)
                    {
                        if (!disconnected)
                        {
                            restarting = false;
                            manager.IsBusy = false;
                            manager.StatusMessage = exception.Message;
                            manager.StatusIsError = true;
                        }
                    }
                }) : null,
                contributionSink: cockpit, settingsRegistry: cockpit.PluginSettings)
            {
                InstalledChanged = installed => _RefreshPluginsAsync(cockpit, server, sessions, installed),
                BeginUiChange = folderId =>
                {
                    _changing.Add(folderId);
                    return new UiChange(_changing, folderId);
                },
            };
            EventHandler stateChanged = (_, _) =>
            {
                var state = server.State;
                Dispatcher.UIThread.Post(async () =>
                {
                    if (_disposed || !restarting)
                    {
                        return;
                    }
                    if (state.KeyRefused)
                    {
                        restarting = false;
                        manager.IsBusy = false;
                        manager.StatusIsError = true;
                        manager.StatusMessage = $"{serverName} refused this window's connect key.";
                        return;
                    }
                    if (!state.IsConnected)
                    {
                        disconnected = true;
                        return;
                    }
                    if (startedAt is not null
                        ? state.Key?.StartedAt is null || state.Key.StartedAt == startedAt
                        : !disconnected)
                    {
                        return;
                    }
                    restarting = false;
                    try
                    {
                        await manager.LoadAsync();
                        await manager.BrowseStoresAsync();
                        manager.NeedsRestart = false;
                        manager.StatusIsError = false;
                        manager.StatusMessage = $"{serverName} restarted and reconnected.";
                    }
                    catch (Exception exception)
                    {
                        manager.StatusMessage = exception.Message;
                        manager.StatusIsError = true;
                    }
                    finally
                    {
                        manager.IsBusy = false;
                    }
                });
            };
            server.StateChanged += stateChanged;
            _stopWatchingServer = () => server.StateChanged -= stateChanged;
            cockpit.UseRemotePlugins(manager);
            await manager.LoadAsync();
            await manager.BrowseStoresAsync();
        }
        catch (Exception exception)
        {
            _loggers.CreateLogger<RemoteWindowUi>().LogWarning(exception, "The server UI could not be activated.");
            cockpit.ToastHost.Add($"Server UI unavailable: {exception.Message}", ToastSeverity.Warning, null, null);
        }
    }

    private async Task _RefreshPluginsAsync(CockpitViewModel cockpit, IRemoteServer server, PluginSessionObserver sessions, IReadOnlyList<InstalledPlugin> installed)
    {
        await _refreshing.WaitAsync();
        try
        {
            if (!_disposed)
            {
                await _SyncPluginsAsync(cockpit, server, sessions, installed);
            }
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private async Task _SyncPluginsAsync(CockpitViewModel cockpit, IRemoteServer server, PluginSessionObserver sessions, IReadOnlyList<InstalledPlugin> installed)
    {
        foreach (var folderId in _unavailable)
        {
            ((IPluginContributionSink)cockpit).RemovePluginContributions(folderId);
        }
        _unavailable.Clear();
        foreach (var folderId in _pluginRuntimes.Keys.ToList())
        {
            if (!installed.Any(row => row.Discovered.FolderId == folderId && row.Registration?.Enabled == true
                && (row.RunningSha256 ?? row.Discovered.Sha256) == _pluginRuntimes[folderId].Sha256))
            {
                if (!_changing.Contains(folderId) && cockpit.Workspaces.HasGuardedPluginWorkspace(folderId))
                {
                    cockpit.ToastHost.Add("The server plugin changed. Close its workspace and refresh Plugins to load its new UI.", ToastSeverity.Warning, null, null);
                    continue;
                }
                ((IPluginContributionSink)cockpit).RemovePluginContributions(folderId);
                _pluginRuntimes[folderId].Runtime.Dispose();
                _pluginRuntimes.Remove(folderId);
            }
        }
        foreach (var row in installed.Where(row => row.Registration?.Enabled == true))
        {
            if (_pluginRuntimes.ContainsKey(row.Discovered.FolderId))
            {
                continue;
            }
            var runtime = new PluginRuntime(row.Discovered.FolderId, _loggers.CreateLogger<RemoteWindowUi>());
            try
            {
                var source = await ServerPluginUiCache.GetAsync(server.Plugins, row);
                if (_disposed)
                {
                    runtime.Dispose();
                    return;
                }
                ((IPluginContributionSink)cockpit).RemovePluginContributions(row.Discovered.FolderId);
                if (source.Manifest.UiAssembly is null)
                {
                    runtime.Dispose();
                    continue;
                }
                if (server.Plugins is not IPluginChannelRelay relay)
                {
                    throw new InvalidOperationException("The server does not offer plugin UI channels.");
                }
                var backend = new RemoteUiBackend(cockpit, sessions, runtime: runtime);
                var host = new CockpitUiHost(source.FolderId, source.Manifest.Name, backend, _windowServices, cockpit,
                    _dialogs, new WindowActions(_owner), sessions, source.Manifest.SecretKeys ?? [], runtime,
                    backend);
                backend.Target = await ServerPluginSettings.OpenScreenHostAsync(host, relay, row.Discovered.FolderId,
                    () => ServerPluginTargets.ProfilesAsync(server.Profiles), () => ServerPluginTargets.ProjectsAsync(server.Projects), server.Name);
                if (_disposed)
                {
                    runtime.Dispose();
                    return;
                }
                var ui = PluginUiManager.ActivateUi(source, null)
                    ?? throw new InvalidOperationException("The plugin UI could not be loaded.");
                if (ui is IDisposable disposable)
                {
                    runtime.Add(disposable);
                }
                ui.InitializeUi(host);
                _pluginRuntimes[row.Discovered.FolderId] = (source.Sha256, runtime);
            }
            catch (Exception exception)
            {
                runtime.Dispose();
                Unavailable(row.Discovered, exception.Message);
            }
        }

        void Unavailable(DiscoveredPlugin plugin, string reason)
        {
            ((IPluginContributionSink)cockpit).RemovePluginContributions(plugin.FolderId);
            _unavailable.Add(plugin.FolderId);
            ((IPluginContributionSink)cockpit).AddPluginSideButton(plugin.FolderId, $"{plugin.Manifest.Name} · UI unavailable",
                () => cockpit.ToastHost.Add(reason, ToastSeverity.Warning, null, null));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _stopWatchingServer?.Invoke();
        foreach (var (_, runtime) in _pluginRuntimes.Values)
        {
            runtime.Dispose();
        }
        _pluginRuntimes.Clear();
        foreach (var lifetime in _lifetimes.AsEnumerable().Reverse())
        {
            lifetime.Dispose();
        }
        _services.Dispose();
    }

    private sealed class WindowServices(IServiceProvider services, Func<CockpitViewModel?> cockpit) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(CockpitViewModel) ? cockpit() : services.GetService(serviceType);
    }

    private sealed class UiChange(HashSet<string> changing, string folderId) : IDisposable
    {
        public void Dispose() => changing.Remove(folderId);
    }

    private sealed class WindowActions(Window owner) : IPluginWindowActions
    {
        public async Task SetClipboardTextAsync(string text)
        {
            var clipboard = owner.Clipboard ?? throw new InvalidOperationException("The clipboard is unavailable.");
            await clipboard.SetTextAsync(text);
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") =>
            new ConfirmationDialog { DataContext = new ConfirmationDialogViewModel(title, message, confirmLabel) }.ShowDialog<bool>(owner);
    }
}
