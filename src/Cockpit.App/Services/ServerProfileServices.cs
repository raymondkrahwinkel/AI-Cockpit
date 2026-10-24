using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Services;

// AC-1585: everything the profile editor asks its services, answered by a server. The editor is the one Options ›
// Profiles uses; only what stands behind it differs. A provider's own config view is a fresh copy of the plugin's UI
// part that reaches the plugin on the server (AC-1571), never this desktop's copy.
public sealed class ServerProfileServices(
    string server,
    IServerProfiles profiles,
    IServerSignIns? signIns = null,
    IPluginAdministration? plugins = null,
    Func<string, (Type UiType, ICockpitUiHost Host)?>? localUiPart = null)
    : ISessionProfileStore, IProfileEditStore, IProfileLoginChecker, ISessionLoginFlows, IMcpServerCatalog, IMcpToolTokenEstimator,
        IModelCatalog, IPluginProviderConfigViews, IPluginRegistrations
{
    private IReadOnlyList<RemoteProfile> _rows = [];
    private IReadOnlyList<RemoteProviderInfo> _providers = [];
    private IReadOnlyList<SessionProviderRegistration> _registrations = [];
    private IReadOnlyDictionary<string, Func<string?, IPluginProviderConfigView>> _views = new Dictionary<string, Func<string?, IPluginProviderConfigView>>();

    // Read once per opening, before the editor is built: its provider list is fixed when it is made.
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        _providers = await profiles.ListProviderInfoAsync(cancellationToken);
        _registrations = [.. _providers.Select(_Registration)];
        _views = await _OpenConfigViewsAsync();
    }

    // The editor, with this server behind every service it asks.
    public ManageProfilesDialogViewModel CreateEditor() =>
        new(this, this, this, this, this, this, null, this, this, hostRunsTty: false) { BrowsesThisMachine = false };

    public async Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        _rows = await profiles.ListAsync(cancellationToken);
        return [.. _rows.Select(row => row.Full).OfType<SessionProfile>()];
    }

    public Task SaveAsync(IReadOnlyList<SessionProfile> list, CancellationToken cancellationToken = default) =>
        SaveEditsAsync([.. list.Select(profile => ((string?)(_Row(profile.Label)?.Label), profile))], cancellationToken);

    // Made and changed first, then removed: the server keeps at least one profile.
    public async Task SaveEditsAsync(IReadOnlyList<(string? OriginalLabel, SessionProfile Profile)> list, CancellationToken cancellationToken = default)
    {
        foreach (var (original, profile) in list)
        {
            await profiles.SaveProfileAsync(original, profile, cancellationToken);
        }

        var kept = list.Select(entry => entry.OriginalLabel).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in _rows.Where(row => !kept.Contains(row.Label)))
        {
            await profiles.DeleteAsync(removed.Label, cancellationToken);
        }
    }

    public bool IsLoggedIn(SessionProfile profile) => _Row(profile.Label)?.SignIn == ProfileSignInKind.SignedIn;

    // Only a profile the server already has and checks a sign-in for; the flow runs there (AC-1357).
    public bool CanStartLogin(SessionProfile profile) => signIns is not null && _Row(profile.Label)?.SignIn is not null;

    public ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken) =>
        CanStartLogin(profile) ? signIns?.Start(server, profile.Label, cancellationToken) : null;

    public async Task<IReadOnlyList<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default) =>
        [.. (await profiles.ListMcpServersAsync(cancellationToken)).Select(name => new McpServerConfig { Name = name })];

    public Task<IReadOnlyList<McpServerConfig>> GetServersForProjectAsync(string? projectId, CancellationToken cancellationToken = default) =>
        GetServersAsync(cancellationToken);

    public Task<McpServerToolEstimate> EstimateAsync(string serverName, bool refresh = false, CancellationToken cancellationToken = default) =>
        profiles.EstimateMcpToolsAsync(serverName, refresh, cancellationToken);

    // A masked key is the one the server holds for the profile at that address; the server checks the address again.
    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey = null, CancellationToken cancellationToken = default) =>
        profiles.ListModelsAsync(baseUrl, apiKey, _rows.FirstOrDefault(row => row.Full?.ProviderConfig is LmStudioConfig held && held.BaseUrl == baseUrl)?.Label, cancellationToken);

    public Task<bool> ProbeAsync(string baseUrl, string? apiKey, TimeSpan timeout) => Task.FromResult(false);

    public void Register(string providerId, Func<string?, IPluginProviderConfigView> createView)
    {
    }

    public Func<string?, IPluginProviderConfigView>? Find(string providerId) => _views.GetValueOrDefault(providerId);

    public IReadOnlyList<SessionProviderRegistration> SessionProviders => _registrations;

    public SessionProviderRegistration? SessionProvider(string providerId) => _registrations.FirstOrDefault(registration => registration.ProviderId == providerId);

    public TtyProviderRegistration? TtyProvider(string providerId) => null;

    public IReadOnlyList<ConversationPickerRegistration> ConversationPickers => [];

    public IReadOnlyList<ProjectMemorySource> MemorySources => [];

    public IReadOnlyList<ProjectMemorySourceRegistration> MemorySourceRegistrations => [];

    public IReadOnlyList<ProjectMemorySourceFamily> MemorySourceFamilies => [];

    public IReadOnlyList<ProjectFieldRegistration> ProjectFields => [];

    public IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? FieldOwnership(string projectId) => null;

    public IReadOnlyList<ISharedProjectSource> SharedProjectSources => [];

    private RemoteProfile? _Row(string label) =>
        _rows.FirstOrDefault(row => string.Equals(row.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));

    // What the editor reads of a provider; it never starts a session from here.
    private static SessionProviderRegistration _Registration(RemoteProviderInfo provider) =>
        new(provider.Id, provider.Label, _ => throw new InvalidOperationException("A server's provider starts its sessions on the server."),
            new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: provider.SupportsPermissions) { SupportsEnvVars = provider.SupportsEnvVars })
        {
            Options = [.. provider.Options.Select(option => new PluginSessionLaunchOption(option.Key, option.Label, option.Choices) { ChoiceLabels = option.ChoiceLabels })],
        };

    // Per plugin that owns a provider on the server and has its UI part on this desktop, one fresh copy against the server.
    private async Task<IReadOnlyDictionary<string, Func<string?, IPluginProviderConfigView>>> _OpenConfigViewsAsync()
    {
        Dictionary<string, Func<string?, IPluginProviderConfigView>> views = new(StringComparer.Ordinal);
        if (plugins is not IPluginChannelRelay relay || localUiPart is null)
        {
            return views;
        }

        var installed = await plugins.GetInstalledAsync();
        foreach (var owner in _providers.Select(provider => provider.Owner).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (installed.FirstOrDefault(plugin => plugin.Discovered.FolderId == owner) is not { } plugin
                || localUiPart(plugin.Discovered.Manifest.Id) is not { } part)
            {
                continue;
            }

            foreach (var (providerId, create) in await ServerPluginSettings.OpenProviderViewsAsync(part.UiType, part.Host, relay, owner, () => Task.FromResult<IReadOnlyList<PluginProfileInfo>>([])))
            {
                views[providerId] = create;
            }
        }

        return views;
    }

}
