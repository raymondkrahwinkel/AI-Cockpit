using Microsoft.Extensions.Logging;
using Cockpit.App.Composition;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.App;

// AC-1487: all `--remote` starts short of Avalonia's own run, shared with its journey: whatever is later added to
// this route (a backend, a plugin, a listener) is inside what the journey measures. Begun and disposed on one thread.
internal sealed class RemoteStartup : IDisposable
{
    private readonly SingleInstanceGuard _singleInstance;
    private readonly InstallationInstanceGuard _installation;
    private readonly ILoggerFactory _loggers;
    private RemoteInstanceConnection? _connection;
    private readonly string _localRoot;

    private RemoteStartup(string server, string localRoot, SingleInstanceGuard singleInstance, ILoggerFactory loggers, LocalRegistry? registry, string? refusal)
    {
        Server = server;
        _localRoot = localRoot;
        _singleInstance = singleInstance;
        _loggers = loggers;
        Logger = loggers.CreateLogger("Cockpit.App.RemoteWindow");
        _installation = InstallationInstanceGuard.Acquire(loggers.CreateLogger<InstallationInstanceGuard>());
        Registry = registry;
        Refusal = refusal;
    }

    public string Server { get; }

    public ILogger Logger { get; }

    // Null when the local registry could not be read; `Refusal` then says why.
    public LocalRegistry? Registry { get; }

    public string? Refusal { get; private set; }

    public ILoggerFactory Loggers => _loggers;

    public bool NeedsUnlock => Registry?.IsLocked == true;

    public ISecretProtectionService? Protection => Registry?.Protection;

    // Moves the root off the local one first, then claims it: one window per server. Null when another holds that claim.
    // `loggersAt` opens the log at the path it is given, only once the claim is held, so a refused start truncates nothing.
    public static RemoteStartup? Begin(string server, Func<string, ILoggerFactory> loggersAt)
    {
        var localRoot = CockpitBuild.StateRoot;
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, RemoteInstance.StateRootFor(localRoot, server));
        if (SingleInstanceGuard.TryAcquire(CockpitBuild.IsDevelopment) is not { } singleInstance)
        {
            return null;
        }

        var loggers = loggersAt(CockpitBuild.LogPath);
        LocalRegistry? registry = null;
        string? refusal = null;
        try
        {
            registry = LocalRegistry.ReadAsync(localRoot).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            refusal = exception.Message;
        }

        return new RemoteStartup(server, localRoot, singleInstance, loggers, registry, refusal);
    }

    // The window's view model over its one server, on the UI thread; null, with `Refusal` set, when there is no key for it.
    public CockpitViewModel? Open(
        Func<CockpitViewModel, string?, Task>? presentOptions = null,
        Func<PluginManagerViewModel, PluginStoreFilter?, Task>? presentPluginStore = null,
        Func<string, string, string, Task<bool>>? confirm = null,
        Func<ProjectDialogViewModel, Task>? presentProjectDialog = null,
        RemoteWindowUi? ui = null)
    {
        if (Registry?.Connect(Server, _loggers) is not { } connection)
        {
            Refusal ??= $"This cockpit holds no connect key for \"{Server}\". Connect to it first, under Options → Security → Connect to a server.";
            return null;
        }

        _connection = connection;
        return CockpitViewModel.ForRemoteWindow(
            Server, connection.Servers, connection.Nodes, new RemoteServerSignIns(connection.Servers), presentOptions, presentPluginStore, confirm, presentProjectDialog, ui);
    }

    internal async Task InitializeUiAsync(RemoteWindowUi ui, CockpitViewModel cockpit)
    {
        if (_connection is { } connection)
        {
            await ui.InitializeAsync(cockpit, connection.Servers, Server, _localRoot);
        }
    }

    public void Dispose()
    {
        _connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _installation.Dispose();
        _singleInstance.Dispose();
        _loggers.Dispose();
    }
}
