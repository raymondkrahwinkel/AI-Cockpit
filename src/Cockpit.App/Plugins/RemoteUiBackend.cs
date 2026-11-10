using Cockpit.App.ViewModels;
using System.Text.Json;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Plugins;

// AC-2283: only CockpitUiHost receives this adapter; no backend plugin is constructed or initialized here.
internal sealed class RemoteUiBackend(CockpitViewModel cockpit, ICockpitSessionObserver sessions, IPluginStorage? presentationStorage = null, PluginRuntime? runtime = null) : ICockpitHost, IPluginUiChannel
{
    private ICockpitUiHost? _target;
    public ICockpitUiHost Target
    {
        private get => _target ?? throw new InvalidOperationException("The remote UI channel is not connected.");
        set => _target = value;
    }

    public IServiceProvider Services => throw new InvalidOperationException("A remote UI has no local backend services.");
    public ICockpitActions Actions => throw new InvalidOperationException("A remote UI has no local backend actions.");
    public IPluginStorage Storage => presentationStorage ?? Target.Storage;
    public ICockpitSessionObserver Sessions => sessions;
    public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
        Target.Channel.InvokeAsync(action, payload, cancellationToken);
    public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler)
    {
        var subscription = Target.Channel.Subscribe(name, handler);
        runtime?.Add(subscription);
        return subscription;
    }

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        cockpit.ToastHost.Add(message, severity switch
        {
            PluginToastSeverity.Success => ToastSeverity.Success,
            PluginToastSeverity.Warning => ToastSeverity.Warning,
            PluginToastSeverity.Error => ToastSeverity.Error,
            _ => ToastSeverity.Information,
        }, actionLabel, onAction);

    public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => Target.GetProfilesAsync();
    public Task<IReadOnlyList<PluginProjectInfo>> GetProjectsAsync() => Target.GetProjectsAsync();
    public Task SendToSessionAsync(string paneId, string text) => Target.SendToSessionAsync(paneId, text);
    public async Task<bool> InsertIntoSessionAsync(string paneId, string text)
    {
        await Target.InsertIntoSessionAsync(paneId, text);
        return true;
    }
    public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
        Target.GetProjectMemoryRowsAsync(paneId, cancellationToken);
    public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        Target.SendIntent(targetPluginId, action, data);
    public bool CanSendIntent(string targetPluginId, string action) => Target.CanSendIntent(targetPluginId, action);
    public string? ResolveManagedCliPath(string cliName) => Target.ResolveManagedCliPath(cliName);
    public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
        Target.GetManagedCliStatusAsync(cliName, cancellationToken);
    public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
        Target.InstallManagedCliAsync(cliName, cancellationToken);
    public bool RemoveManagedCli(string cliName) => Target.RemoveManagedCli(cliName);
    public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) =>
        Target.GetManagedCliAutoUpdateAsync(cliName, cancellationToken);
    public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) =>
        Target.SetManagedCliAutoUpdateAsync(cliName, enabled, cancellationToken);
    public Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) =>
        Target.GetMcpServerAuthStateAsync(name, cancellationToken);
    public Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        Target.SignInMcpServerAsync(name, cancellationToken);
    public Task SignOutMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        Target.SignOutMcpServerAsync(name, cancellationToken);
}
