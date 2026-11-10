using Avalonia.Controls;
using Avalonia.Input.Platform;
using Cockpit.App.Docking;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Plugins;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Composition;

// AC-2283: the ordinary window's UI registries, without a backend container or any hosted services.
internal sealed class RemoteWindowUi : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly Window _owner;
    private readonly SurfaceWindows _surfaces;
    private readonly ILoggerFactory _loggers;
    private readonly List<IDisposable> _lifetimes = [];
    private readonly IPluginDialogHost _dialogs;
    private CockpitViewModel? _cockpit;
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
        services.AddSingleton(_ => _cockpit ?? throw new InvalidOperationException("The remote window is not open."));
        services.AddSingleton<IWorkspaceTypeRegistry, WorkspaceTypeRegistry>();
        services.AddSingleton<IWidgetRegistry, WidgetRegistry>();
        services.AddSingleton<IDockPanelRegistry, DockPanelRegistry>();
        services.AddSingleton<ICompanionToolRegistry, CompanionToolRegistry>();
        services.AddSingleton<IConversationPickerRegistry, ConversationPickerRegistry>();
        services.AddSingleton<IPluginProviderConfigViews, PluginProviderConfigViews>();
        _services = services.BuildServiceProvider();
    }

    internal IWorkspaceTypeRegistry WorkspaceTypes => _services.GetRequiredService<IWorkspaceTypeRegistry>();
    internal IWidgetRegistry Widgets => _services.GetRequiredService<IWidgetRegistry>();
    internal IDockPanelRegistry DockPanels => _services.GetRequiredService<IDockPanelRegistry>();

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
            var installed = await server.Plugins.GetInstalledAsync();
            var sources = await new PluginBootstrap(Path.Combine(localRoot, "plugins"), Path.Combine(localRoot, "cockpit.json"))
                .DiscoverAsync(AbstractionsContract.Version);
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
                _services, cockpit, _dialogs, actions, sessions, [], fileRuntime,
                new RemotePluginUiChannel((IPluginChannelRelay)server.Plugins, "server-project-files"));
            fileRuntime.Add(RemoteFileUi.Attach(fileHost, server));
            _lifetimes.Add(fileRuntime);
            foreach (var row in installed.Where(row => row.Registration?.Enabled == true))
            {
                var source = sources.FirstOrDefault(candidate => candidate.Manifest.Id == row.Discovered.Manifest.Id);
                var reason = source switch
                {
                    null => "Install this plugin's UI on this desktop to use it here.",
                    { Decision: not PluginLoadDecision.Load } => $"The desktop UI is unavailable: {source.Decision}.",
                    { Manifest.UiAssembly: null } => "This plugin does not supply a separate UI assembly.",
                    _ when ServerPluginVersions.Differ(source.Manifest.Version, row.RunningVersion ?? row.Discovered.Manifest.Version) =>
                        ServerPluginVersions.Message(source.Manifest.Name, serverName, row.RunningVersion ?? row.Discovered.Manifest.Version, source.Manifest.Version),
                    _ => null,
                };
                if (reason is not null || source is null)
                {
                    Unavailable(row.Discovered, reason ?? "The desktop UI is unavailable.");
                    continue;
                }
                var runtime = new PluginRuntime(source.FolderId, _loggers.CreateLogger<RemoteWindowUi>());
                try
                {
                    if (server.Plugins is not IPluginChannelRelay relay)
                    {
                        throw new InvalidOperationException("The server does not offer plugin UI channels.");
                    }
                    var backend = new RemoteUiBackend(cockpit, sessions, runtime: runtime);
                    var host = new CockpitUiHost(source.FolderId, source.Manifest.Name, backend, _services, cockpit,
                        _dialogs, new WindowActions(_owner), sessions, source.Manifest.SecretKeys ?? [], runtime,
                        backend);
                    backend.Target = await ServerPluginSettings.OpenScreenHostAsync(host, relay, row.Discovered.FolderId,
                        () => ServerPluginTargets.ProfilesAsync(server.Profiles), () => ServerPluginTargets.ProjectsAsync(server.Projects), serverName);
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
                    _lifetimes.Add(runtime);
                }
                catch (Exception exception)
                {
                    runtime.Dispose();
                    Unavailable(row.Discovered, exception.Message);
                }
            }
        }
        catch (Exception exception)
        {
            _loggers.CreateLogger<RemoteWindowUi>().LogWarning(exception, "The server UI could not be activated.");
            cockpit.ToastHost.Add($"Server UI unavailable: {exception.Message}", ToastSeverity.Warning, null, null);
        }

        void Unavailable(DiscoveredPlugin plugin, string reason) =>
            ((IPluginContributionSink)cockpit).AddPluginSideButton(plugin.FolderId, $"{plugin.Manifest.Name} · UI unavailable",
                () => cockpit.ToastHost.Add(reason, ToastSeverity.Warning, null, null));
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var lifetime in _lifetimes.AsEnumerable().Reverse())
        {
            lifetime.Dispose();
        }
        _services.Dispose();
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
