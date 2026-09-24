using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.ManagedCli;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workflows;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Infrastructure.Plugins;

// AC-1392: the `ICockpitHost` a plugin's backend part receives, scoped by `pluginId`, with no window behind it.
// Sessions are reached through ISessionRegistry/ISessionLauncher, which marshal for themselves. The window members
// are no-ops that say so once per plugin; the desktop's DesktopPluginHost overrides them until F2.14 (AC-1369).
public class PluginBackendHost(
    string pluginId,
    string pluginName,
    IServiceProvider services,
    IPluginStorage storage,
    ICockpitSessionObserver sessions,
    ICockpitActions actions,
    PluginDiagnostics diagnostics,
    Type? ownPluginType = null,
    IPluginCache? cache = null) : ICockpitHost
{
    // Without a cache file behind it — a test host — the plugin still gets a cache, one that lives as long as
    // this host does. `Cache` is never null (AC-1294).
    private readonly IPluginCache _cache = cache ?? new InMemoryPluginCache();

    // This plugin's open channels, keyed by the id it named them (AC-1023). Kept so re-opening one replaces it
    // rather than leaving a second gateway subscribed to the same transcript, doubling every relayed row.
    private readonly Dictionary<string, IAssistantChannelGateway> _assistantChannels = new(StringComparer.Ordinal);

    private int _saidWindowless;

    // The plugin's folder id and manifest name, for a derived host's own registrations and log lines.
    protected string PluginId => pluginId;

    protected string PluginName => pluginName;

    public IServiceProvider Services => services;

    public ICockpitActions Actions => actions;

    public IPluginStorage Storage => storage;

    public IPluginCache Cache => _cache;

    // AC-1389: this plugin's slice of the backend's channel hub, which the UI part's CockpitUiHost reaches by the same id.
    public IPluginBackendChannel Channel => services.GetRequiredService<PluginChannelHub>().For(pluginId);

    public ICockpitSessionObserver Sessions => sessions;

    // AC-128: the transport-verified pane behind the current in-process MCP call, read from the ambient request
    // context the auth middleware set. A plugin's own MCP tool keys on this so it acts on the calling session, not a
    // session id the agent named. Null off the verified path (no MCP call in flight).
    public string? CurrentMcpCallerPaneId => McpRequestContext.CurrentPaneId;

    // The legacy typed Claude profile's own models and effort levels, which only the desktop's option catalog holds.
    protected virtual IReadOnlyList<string> LegacyClaudeModels => [];

    protected virtual IReadOnlyList<string> LegacyClaudeEfforts => [];

    // The one hop the assistant's gateway needs: none here, the desktop's goes through its UI thread (AC-1379).
    protected virtual IAssistantSessionHost AssistantHostFor(IAssistantSessionHost host) => host;

    public virtual bool HasSettings => false;

    public virtual Task ShowSettingsAsync() => Task.CompletedTask;

    public virtual void OnSettingsSaved(Action callback) => _NoWindow(nameof(OnSettingsSaved));

    public virtual void AddSideMenuButton(string title, Action onInvoke) => _NoWindow(nameof(AddSideMenuButton));

    public virtual SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke)
    {
        _NoWindow(nameof(AddSideMenuButtonWithBadge));
        return new SideMenuButtonBadge();
    }

    public virtual void AddShortcut(PluginShortcut shortcut) => _NoWindow(nameof(AddShortcut));

    public virtual void AddSessionHeaderAction(PluginSessionAction action) => _NoWindow(nameof(AddSessionHeaderAction));

    public virtual void AddSupervisedActivityProvider(ISupervisedActivitySource source) =>
        _NoWindow(nameof(AddSupervisedActivityProvider));

    public virtual void AddToolbarAction(ToolbarAction action) => _NoWindow(nameof(AddToolbarAction));

    public virtual void AddWidget(WidgetRegistration registration) => _NoWindow(nameof(AddWidget));

    public virtual IReadOnlyList<WidgetRegistration> Widgets => [];

    public virtual void AddDockPanel(DockPanelRegistration registration) => _NoWindow(nameof(AddDockPanel));

    public virtual void AddCompanionTool(CompanionToolRegistration registration) => _NoWindow(nameof(AddCompanionTool));

    public virtual IReadOnlyList<CompanionToolRegistration> CompanionTools => [];

    public virtual void AddWorkspaceType(WorkspaceTypeRegistration registration) => _NoWindow(nameof(AddWorkspaceType));

    public virtual IReadOnlyList<WorkspaceTypeRegistration> WorkspaceTypes => [];

    public virtual Task OpenWorkspaceAsync(string workspaceTypeId)
    {
        _NoWindow(nameof(OpenWorkspaceAsync));
        return Task.CompletedTask;
    }

    // Exactly one callback, as the contract promises: with no dialog to show, nothing started.
    public virtual Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null)
    {
        _NoWindow(nameof(ShowNewSessionDialogAsync));
        onCancelled?.Invoke();
        return Task.CompletedTask;
    }

    public virtual void OpenHelp(string article, string? section = null) => _NoWindow(nameof(OpenHelp));

    public virtual bool HasHelp(string article, string? section = null) => false;

    // A toast with nobody to read it still says something, so it goes to the log at its own severity.
    public virtual void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        _Logger()?.Log(
            severity switch
            {
                PluginToastSeverity.Error => LogLevel.Error,
                PluginToastSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Information,
            },
            "Plugin {PluginId}: {Message}",
            pluginId,
            message);

    public IAssistantChannelGateway? OpenAssistantChannel(AssistantChannelContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);

        // GetService, not GetRequiredService: a host built without an assistant (tests, a headless run) has no
        // channel to offer, which the contract says is a null rather than a throw.
        if (services.GetService<IAssistantSessionHost>() is not { } assistantHost)
        {
            return null;
        }

        if (_assistantChannels.Remove(contribution.Id, out var previous))
        {
            previous.Dispose();
        }

        var gateway = new AssistantChannelGateway(
            contribution,
            AssistantHostFor(assistantHost),
            services.GetRequiredService<IConsentBroker>(),
            services.GetRequiredService<ILogger<AssistantChannelGateway>>());
        _assistantChannels[contribution.Id] = gateway;

        return gateway;
    }

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) =>
        RequestConsentAsync(request, CancellationToken.None);

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) =>
        // The plugin's identity is stamped here, not taken from the request — a plugin cannot ask under another's name.
        services.GetRequiredService<IConsentBroker>()
            .RequestConsentAsync(request with { Source = request.Source with { PluginId = pluginId } }, cancellationToken);

    public void AddConversationPicker(ConversationPickerRegistration picker) =>
        services.GetRequiredService<IConversationPickerRegistry>().Register(picker);

    public void AddProjectField(ProjectFieldRegistration registration)
    {
        // Refused means another plugin already registered this key. That is the agreed case, not a mistake — the
        // GitHub Issues and Pull Requests plugins both offer "which repository" so either one alone still shows the
        // field — so this is logged at debug level, unlike the widget/workspace clashes.
        if (!services.GetRequiredService<IProjectFieldRegistry>().Register(registration))
        {
            _Logger()?.LogDebug("Project field '{ProjectFieldKey}' is already contributed; this registration is ignored", registration.Key);
        }
    }

    public IReadOnlyList<ProjectFieldRegistration> ProjectFields =>
        services.GetRequiredService<IProjectFieldRegistry>().Fields;

    public void ClaimProjectOwnership(ProjectOwnershipRegistration registration)
    {
        // Refused means another plugin already claims this project. That is the agreed case, not a mistake — the
        // same reason AddProjectField logs at debug level rather than warning.
        if (!services.GetRequiredService<IProjectOwnershipRegistry>().Register(registration))
        {
            _Logger()?.LogDebug(
                "Project '{ProjectId}' ownership is already claimed by another plugin; this registration is ignored",
                registration.ProjectId);
        }
    }

    public IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? GetProjectFieldOwnership(string projectId) =>
        services.GetRequiredService<IProjectOwnershipRegistry>().Resolve(projectId);

    public void AddProjectMemorySource(ProjectMemorySourceRegistration registration)
    {
        // Refused means another plugin already contributes this scheme — agreement, not a clash, the same reason
        // AddProjectField logs at debug rather than warning.
        if (!services.GetRequiredService<IProjectMemorySourceRegistry>().Register(registration))
        {
            _Logger()?.LogDebug("Memory source '{MemorySourceScheme}' is already contributed; this registration is ignored", registration.Scheme);
        }
    }

    public void RemoveProjectMemorySource(string scheme) =>
        services.GetRequiredService<IProjectMemorySourceRegistry>().Remove(scheme);

    public IReadOnlyList<ProjectMemorySourceRegistration> ProjectMemorySources =>
        services.GetRequiredService<IProjectMemorySourceRegistry>().Sources;

    public void AddProjectMemorySourceFamily(ProjectMemorySourceFamily family)
    {
        // Refused means another plugin already declared this key — agreement, not a clash, the same reason
        // AddProjectMemorySource logs at debug rather than warning.
        if (!services.GetRequiredService<IProjectMemorySourceRegistry>().RegisterFamily(family))
        {
            _Logger()?.LogDebug("Memory source family '{MemorySourceFamilyKey}' is already declared; this registration is ignored", family.Key);
        }
    }

    public void AddSharedProjectSource(ISharedProjectSource source)
    {
        // Refused means another plugin already contributes this key — agreement, not a clash, the same reason
        // AddProjectMemorySource logs at debug rather than warning.
        if (!services.GetRequiredService<ISharedProjectSourceRegistry>().Register(source))
        {
            _Logger()?.LogDebug("Shared-project source '{SharedProjectSourceKey}' is already contributed; this registration is ignored", source.Key);
        }
    }

    public void RemoveSharedProjectSource(string key) =>
        services.GetRequiredService<ISharedProjectSourceRegistry>().Remove(key);

    public IReadOnlyList<ISharedProjectSource> SharedProjectSources =>
        services.GetRequiredService<ISharedProjectSourceRegistry>().Sources;

    public async Task<string?> GetProjectFieldValueAsync(string key, string? paneId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || await _ProjectOfAsync(paneId, cancellationToken) is not { } projectId)
        {
            return null;
        }

        var projects = await services.GetRequiredService<IProjectStore>().LoadAsync(cancellationToken);
        return projects.Find(projectId)?.LinkedAs(key);
    }

    public async Task<IReadOnlyList<string>> GetProjectFieldValuesAsync(string key, string? paneId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || await _ProjectOfAsync(paneId, cancellationToken) is not { } projectId)
        {
            return [];
        }

        var projects = await services.GetRequiredService<IProjectStore>().LoadAsync(cancellationToken);
        return projects.Find(projectId)?.LinkedAsAll(key) ?? [];
    }

    public async Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId, CancellationToken cancellationToken)
    {
        if (await _ProjectOfAsync(paneId, cancellationToken) is not { } projectId)
        {
            return [];
        }

        var projects = await services.GetRequiredService<IProjectStore>().LoadAsync(cancellationToken);
        var resources = projects.Find(projectId)?.Resources ?? [];
        return [.. resources
            .Where(resource => resource.Role == ProjectResourceRole.Memory)
            .Select(resource => new ProjectMemoryRow(resource.Reference, resource.Label, resource.ReachesSessions))];
    }

    // No pane named falls back to the selected one, which only a desktop has. Which project a pane belongs to is one
    // question with one answer (AC-320); a host without that resolver asks the pane's own handle.
    private async Task<string?> _ProjectOfAsync(string? paneId, CancellationToken cancellationToken)
    {
        var pane = string.IsNullOrEmpty(paneId) ? sessions.ActivePaneId : paneId;
        if (string.IsNullOrEmpty(pane))
        {
            return null;
        }

        var projectId = services.GetService<ISessionProjectResolver>() is { } resolver
            ? await resolver.ProjectIdOfAsync(pane, cancellationToken)
            : services.GetService<ISessionRegistry>()?.Find(pane)?.ProjectId;
        return string.IsNullOrEmpty(projectId) ? null : projectId;
    }

    public void AddTrackerProvider(ITrackerProvider provider)
    {
        // First registration for a tracker id wins; a later one is logged and ignored rather than added beside it.
        if (!services.GetRequiredService<ITrackerProviderRegistry>().Register(provider))
        {
            _Logger()?.LogWarning("Tracker '{TrackerId}' is already contributed by another plugin; this registration is ignored", provider.TrackerId);
        }
    }

    public IReadOnlyList<ITrackerProvider> TrackerProviders =>
        services.GetRequiredService<ITrackerProviderRegistry>().Providers;

    public void AddSessionResourceProvider(ISessionResourceProvider provider)
    {
        // Refused means this exact provider is already registered — a plugin whose Initialize ran twice, not two
        // plugins clashing. Nothing is lost by ignoring it, so this is a debug line rather than a warning.
        if (!services.GetRequiredService<ISessionResourceProviderRegistry>().Register(provider))
        {
            _Logger()?.LogDebug("Session-resource provider {Provider} is already registered; this registration is ignored", provider.GetType().Name);
        }
    }

    public IReadOnlyList<ISessionResourceProvider> SessionResourceProviders =>
        services.GetRequiredService<ISessionResourceProviderRegistry>().Providers;

    public void AddWorkflowStep(IWorkflowStep step) =>
        services.GetRequiredService<IWorkflowStepRegistry>().Register(step);

    public IReadOnlyList<IWorkflowStep> WorkflowSteps =>
        services.GetRequiredService<IWorkflowStepRegistry>().Steps;

    // The contributing plugin's own name is the heading a template is filed under, unless it says otherwise: that is
    // where an operator looks for "the YouTrack one".
    public void AddWorkflowTemplate(WorkflowTemplate template) =>
        services.GetRequiredService<IWorkflowTemplateRegistry>()
            .Register(template with { Category = template.Category ?? pluginName });

    public IReadOnlyList<WorkflowTemplate> WorkflowTemplates =>
        services.GetRequiredService<IWorkflowTemplateRegistry>().Templates;

    public void RaiseWorkflowTrigger(string typeId, IReadOnlyDictionary<string, string> data) =>
        services.GetRequiredService<IWorkflowStepRegistry>().Raise(typeId, data);

    public event EventHandler<WorkflowTriggerFired>? WorkflowTriggerRaised
    {
        add => services.GetRequiredService<IWorkflowStepRegistry>().Fired += value;
        remove => services.GetRequiredService<IWorkflowStepRegistry>().Fired -= value;
    }

    // The caller's id is stamped here from this host's own pluginId, never taken from the caller — a plugin cannot
    // register a handler as, or send an intent under, another plugin's name (same rule as RequestConsentAsync).
    public void RegisterIntentHandler(string action, Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>> handler) =>
        services.GetRequiredService<IPluginIntentRegistry>().Register(pluginId, action, handler);

    public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        services.GetRequiredService<IPluginIntentRegistry>()
            .Dispatch(new PluginIntent(pluginId, targetPluginId, action, data));

    public bool CanSendIntent(string targetPluginId, string action) =>
        services.GetRequiredService<IPluginIntentRegistry>().HasHandler(targetPluginId, action);

    // The loaded plugins by their host-stamped FolderId (the same id stamped on intents and template registrations) and
    // their manifest name, so a plugin can show a readable name for another plugin's id. GetService, not required: the
    // manager is absent in some hosting/test paths, in which case there is simply nothing to attribute.
    public IReadOnlyList<PluginMetadata> InstalledPlugins =>
        services.GetService<PluginManager>() is { } manager
            ? [.. manager.Loaded.Select(plugin => new PluginMetadata(
                plugin.FolderId,
                plugin.Manifest.Name,
                plugin.Manifest.Version,
                plugin.Manifest.Author,
                plugin.Manifest.Description))]
            : [];

    // The owner id is stamped here from this host's own pluginId, never taken from the caller — a plugin cannot
    // register a template under another plugin's name (same rule as RegisterIntentHandler above).
    public void RegisterAutopilotTemplate(PluginAutopilotTemplate template) =>
        services.GetRequiredService<IAutopilotTemplateRegistry>().Register(pluginId, template);

    public IReadOnlyList<RegisteredAutopilotTemplate> RegisteredAutopilotTemplates =>
        services.GetRequiredService<IAutopilotTemplateRegistry>().Registrations;

    public void AddSessionProvider(SessionProviderRegistration registration) =>
        services.GetRequiredService<IPluginProviderRegistry>().Register(registration);

    public void AddTtyProvider(TtyProviderRegistration registration) =>
        services.GetRequiredService<IPluginTtyProviderRegistry>().Register(registration);

    public async Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync()
    {
        var profiles = await services.GetRequiredService<ISessionProfileStore>().LoadAsync().ConfigureAwait(false);
        var registry = services.GetRequiredService<IPluginProviderRegistry>();
        return profiles
            .Select(profile =>
            {
                var model = _DeclaredModelOption(registry, profile);
                var effort = _DeclaredEffortOption(registry, profile);
                return new PluginProfileInfo(profile.Label, profile.Provider.ToString(), profile.Claude?.ConfigDir ?? string.Empty)
                {
                    // AC-256: asks the provider for its models instead of keeping the host's own copy of the
                    // Claude aliases, which had drifted out of cheapest-first order. Falls back to the catalogue
                    // only when there is no registration to ask (unloaded provider plugin, legacy typed config).
                    ModelSuggestions = model?.Choices ?? (profile.Claude is not null ? LegacyClaudeModels : []),
                    // Cost is the provider's own estimate or nothing at all; the host never ranks or prices a model.
                    ModelCostEstimatesCheapestFirst = model?.CostEstimatesCheapestFirst ?? [],
                    // AC-1342: the same declared schema list_profiles and start_agent's option check read — a
                    // provider's own KnownValues for "effort", not a host-owned list. Null KnownValues (free-form or
                    // resolved only once live, Codex's own) reports empty, same as declaring no effort option at all.
                    EffortSuggestions = effort?.KnownValues?.Select(value => value.Value).ToList()
                        ?? (profile.Claude is not null ? LegacyClaudeEfforts : []),
                    // The local, free-to-run providers; everything else (Claude, Codex, hosted plugin providers) is a paid API.
                    RunsLocally = profile.Provider is Core.Profiles.SessionProvider.Ollama or Core.Profiles.SessionProvider.LmStudio,
                };
            })
            .ToList();
    }

    // The profile's model launch option, if its provider declares one, found via the well-known `Model`
    // key. Reads only statically declared options, not `ResolveOptionsAsync` — that hits a CLI, and this
    // runs on every plan emission and step start, where a stall would be felt.
    private static PluginSessionLaunchOption? _DeclaredModelOption(IPluginProviderRegistry registry, Core.Profiles.SessionProfile profile) =>
        profile.ProviderConfig is Core.Profiles.PluginProviderConfig plugin
            ? registry.Resolve(plugin.ProviderId)?.Options.FirstOrDefault(option => option.Key == WellKnownPluginSessionOptions.Model)
            : null;

    // The profile's declared effort option (AC-1342), if its provider states one — the driver-capability schema
    // (`Capabilities.DeclaredOptions`), the same source `list_profiles` and `start_agent`'s option validation read,
    // not the New-session dialog's launch-option list `_DeclaredModelOption` reads above.
    private static PluginSessionOptionDescriptor? _DeclaredEffortOption(IPluginProviderRegistry registry, Core.Profiles.SessionProfile profile) =>
        profile.ProviderConfig is Core.Profiles.PluginProviderConfig plugin
            ? registry.Resolve(plugin.ProviderId)?.Capabilities.DeclaredOptions.FirstOrDefault(option => option.Key == WellKnownPluginSessionOptions.Effort)
            : null;

    // Idempotent upsert-by-name into `IMcpServerStore` (#60), refreshing only plugin-owned connection
    // fields on repeat calls (Enabled/Scope stay as the operator left them). Fire-and-forget (#184): I/O
    // failures are caught and attributed to this plugin instead of throwing unobserved.
    public async Task AddMcpServer(McpServerContribution contribution)
    {
        var store = services.GetRequiredService<IMcpServerStore>();

        try
        {
            var servers = (await store.LoadAsync().ConfigureAwait(false)).ToList();
            var existingIndex = servers.FindIndex(server => string.Equals(server.Name, contribution.Name, StringComparison.Ordinal));

            if (existingIndex < 0)
            {
                servers.Add(PluginMcpMapping.ToServerConfig(contribution));
            }
            else
            {
                // Refresh only the connection fields; Scope/Enabled stay as the operator left them. Reuses
                // ToServerConfig instead of restating its auth-field-clearing rule (AC-500).
                var refreshed = PluginMcpMapping.ToServerConfig(contribution);
                servers[existingIndex] = servers[existingIndex] with
                {
                    Transport = refreshed.Transport,
                    Url = refreshed.Url,
                    Auth = refreshed.Auth,
                    ApiKey = refreshed.ApiKey,
                    OAuthAuthority = refreshed.OAuthAuthority,
                    OAuthClientId = refreshed.OAuthClientId,
                };
            }

            await store.SaveAsync(servers).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _Logger()?.LogWarning(exception, "Plugin {PluginId}'s MCP server contribution '{ServerName}' failed to register.", pluginId, contribution.Name);
            diagnostics.Record(pluginId, pluginName, "mcp-server", exception.Message);
        }
    }

    // Same fire-and-forget exposure as `AddMcpServer` (#184): a store failure here is caught and attributed to this plugin; resolving the store and a shutdown cancellation are excluded the same way.
    public async Task RemoveMcpServer(string name)
    {
        var store = services.GetRequiredService<IMcpServerStore>();

        try
        {
            var servers = (await store.LoadAsync().ConfigureAwait(false)).ToList();

            // Only write when something actually goes — this runs on every start of a plugin that reclaims its
            // pushed entries, and re-saving an unchanged registry each launch is needless churn.
            if (servers.RemoveAll(server => string.Equals(server.Name, name, StringComparison.Ordinal)) > 0)
            {
                await store.SaveAsync(servers).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _Logger()?.LogWarning(exception, "Plugin {PluginId}'s MCP server removal ('{ServerName}') failed.", pluginId, name);
            diagnostics.Record(pluginId, pluginName, "mcp-server", exception.Message);
        }
    }

    // Looks up the OAuth server for `name` (shared registry, then AC-504 per-plugin fallback for
    // per-project delivery like Depot) and asks `IMcpOAuthCoordinator` non-interactively (AC-243).
    // An unresolved name or missing coordinator answers Unknown rather than throwing.
    public async Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (services.GetService<IMcpOAuthCoordinator>() is not { } coordinator)
        {
            return PluginMcpAuthState.Unknown;
        }

        try
        {
            var server = await _ResolveOAuthServerAsync(name, cancellationToken).ConfigureAwait(false);
            if (server is null)
            {
                return PluginMcpAuthState.Unknown;
            }

            return await coordinator.GetStateAsync(server, cancellationToken).ConfigureAwait(false) switch
            {
                McpAuthState.Authorized => PluginMcpAuthState.Authorized,
                McpAuthState.AuthorizationRequired => PluginMcpAuthState.AuthorizationRequired,
                _ => PluginMcpAuthState.Unknown,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Record(pluginId, pluginName, "mcp-auth-state", exception.Message);
            return PluginMcpAuthState.Unknown;
        }
    }

    // Drives the same interactive loopback sign-in the MCP-servers dialog offers (AC-243/AC-355), reporting
    // only a named outcome — never a token (Iron Law #8) or the dialog's own failure detail. An unmatched
    // name or missing coordinator answers Unavailable without attempting anything.
    public async Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default)
    {
        if (services.GetService<IMcpOAuthCoordinator>() is not { } coordinator)
        {
            return PluginMcpSignInOutcome.Unavailable;
        }

        try
        {
            var server = await _ResolveOAuthServerAsync(name, cancellationToken).ConfigureAwait(false);
            if (server is null)
            {
                return PluginMcpSignInOutcome.Unavailable;
            }

            var access = await coordinator.AcquireAsync(server, interactive: true, cancellationToken).ConfigureAwait(false);
            return access.State == McpAuthState.Authorized ? PluginMcpSignInOutcome.Authorized : PluginMcpSignInOutcome.Declined;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Record(pluginId, pluginName, "mcp-sign-in", exception.Message);
            return PluginMcpSignInOutcome.Unreachable;
        }
    }

    // Calls a tool on this plugin's own MCP server via `IMcpToolInvoker` (AC-502), never opening a browser
    // or exposing the bearer token. Accepts any `name` known to the registry or a plugin's `GetMcpServers`
    // (AC-504); AC-499 scopes the invoker's own caller fallback list to this plugin's contributions only.
    public async Task<PluginMcpToolCallResult> CallMcpToolAsync(
        string name,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _IsKnownMcpServerNameAsync(name, cancellationToken).ConfigureAwait(false))
        {
            return PluginMcpToolCallResult.Unavailable;
        }

        if (services.GetService<IMcpToolInvoker>() is not { } invoker)
        {
            return PluginMcpToolCallResult.Unavailable;
        }

        try
        {
            var result = await invoker.InvokeAsync(name, toolName, arguments, projectId, _OwnMcpServerContributions(), cancellationToken).ConfigureAwait(false);
            return result.Outcome switch
            {
                McpToolInvocationOutcome.Success => PluginMcpToolCallResult.Success(result.Content ?? string.Empty),
                McpToolInvocationOutcome.AuthorizationRequired => PluginMcpToolCallResult.AuthorizationRequired,
                _ => PluginMcpToolCallResult.Failed(_UnwrapToolInvocationError(toolName, result.Error ?? "The tool call failed.")),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Record(pluginId, pluginName, "mcp-tool-call", exception.Message);
            return PluginMcpToolCallResult.Failed(_UnwrapToolInvocationError(toolName, exception.Message));
        }
    }

    // AC-748: the MCP client SDK prefixes failed-call messages with "An error occurred invoking '{toolName}': ".
    // Stripped once here rather than in every caller that pattern-matches the tool's own error text
    // (e.g. Depot's PublishAsync StartsWith("[NotFound]")).
    private static string _UnwrapToolInvocationError(string toolName, string message)
    {
        var prefix = $"An error occurred invoking '{toolName}': ";
        return message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
    }

    // The OAuth server named `name`: shared registry first, then (AC-504) every plugin's project-agnostic
    // `GetMcpServers()` — sign-in has no project to scope by here. A plugin that throws while listing is
    // logged and skipped, same as `McpServerCatalog` does, not fatal to the lookup.
    private async Task<McpServerConfig?> _ResolveOAuthServerAsync(string name, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IMcpServerStore>();
        var fromRegistry = (await store.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal) && candidate.Auth == McpServerAuth.OAuth);

        if (fromRegistry is not null)
        {
            return fromRegistry;
        }

        return services.GetServices<IPluginMcpProvider>()
            .SelectMany(_SafeContributionsOf)
            .Select(PluginMcpMapping.ToServerConfig)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal) && candidate.Auth == McpServerAuth.OAuth);
    }

    // Whether `name` resolves to anything at all — shared registry (any auth kind) or any plugin's
    // `GetMcpServers()` (AC-502 review), same lax cross-plugin scope as `_ResolveOAuthServerAsync`.
    // What matters: a cockpit-internal endpoint can never pass this check.
    private async Task<bool> _IsKnownMcpServerNameAsync(string name, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IMcpServerStore>();
        var inRegistry = (await store.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Any(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

        if (inRegistry)
        {
            return true;
        }

        return services.GetServices<IPluginMcpProvider>()
            .SelectMany(_SafeContributionsOf)
            .Any(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
    }

    private IReadOnlyList<McpServerContribution> _SafeContributionsOf(IPluginMcpProvider provider)
    {
        try
        {
            return provider.GetMcpServers();
        }
        catch (Exception exception)
        {
            _Logger()?.LogWarning(exception, "A plugin failed to list its MCP servers while resolving an OAuth sign-in; leaving them out of the lookup.");
            return [];
        }
    }

    // This plugin's own MCP servers (AC-499), handed to `IMcpToolInvoker`/`IMcpToolProbe` as an additive
    // fallback list. Scoped by `ownPluginType` to only the matching `IPluginMcpProvider` instance(s), never
    // the whole container-wide set, so this can never fall back into another plugin's server.
    private IReadOnlyList<McpServerConfig> _OwnMcpServerContributions()
    {
        if (ownPluginType is null)
        {
            return [];
        }

        return services.GetServices<IPluginMcpProvider>()
            .Where(provider => provider.GetType() == ownPluginType)
            .SelectMany(_SafeContributionsOf)
            .Select(PluginMcpMapping.ToServerConfig)
            .ToList();
    }

    // Delegates to `IMcpToolProbe` (AC-503), mapping its result onto `McpProbeResult`; no probe registered
    // answers Failed without attempting anything. AC-499: also hands the probe `_OwnMcpServerContributions`
    // as fallback, since this call takes no project id — else a per-project plugin (Depot) is unprobeable.
    public async Task<McpProbeResult> ProbeMcpToolAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (services.GetService<IMcpToolProbe>() is not { } probe)
        {
            return McpProbeResult.Failed;
        }

        try
        {
            var result = await probe.ProbeAsync(serverName, toolName, arguments, _OwnMcpServerContributions(), cancellationToken).ConfigureAwait(false);
            return result.Outcome switch
            {
                McpToolProbeOutcome.NotSignedIn => McpProbeResult.NotSignedIn,
                McpToolProbeOutcome.NotFound => McpProbeResult.NotFound,
                McpToolProbeOutcome.Success => McpProbeResult.Success(result.Detail),
                _ => McpProbeResult.Failed,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never token/credential data here (Iron Law #8) — only the server/tool names, which are configuration.
            diagnostics.Record(pluginId, pluginName, "mcp-probe", exception.Message);
            return McpProbeResult.Failed;
        }
    }

    public Task AddMcpEndpoint(string serverName, object tools, Func<bool>? isEnabled = null, bool isInternal = false) =>
        services.GetService<ICockpitMcpEndpointHost>() is { } endpointHost
            ? endpointHost.MountAsync(serverName, tools, isEnabled, isInternal)
            : Task.CompletedTask;

    public void AddManagedCli(ManagedCliDescriptor descriptor) =>
        services.GetRequiredService<IManagedCliService>().Register(descriptor);

    public string? ResolveManagedCliPath(string cliName) =>
        services.GetService<IManagedCliService>()?.ResolveInstalledPath(cliName);

    public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
        services.GetService<IManagedCliService>() is { } managedCli
            ? managedCli.EnsureInstalledAsync(cliName, cancellationToken)
            : Task.FromResult(ManagedCliInstallResult.Fail("Managed CLIs are not available in this host."));

    public bool RemoveManagedCli(string cliName) =>
        services.GetService<IManagedCliService>()?.RemoveInstalled(cliName) ?? false;

    public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
        services.GetService<IManagedCliService>() is { } managedCli
            ? managedCli.GetStatusAsync(cliName, cancellationToken)
            : Task.FromResult(new ManagedCliStatus(null, null));

    public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) =>
        services.GetService<IManagedCliAutoUpdateStore>() is { } autoUpdateStore
            ? autoUpdateStore.IsEnabledAsync(cliName, cancellationToken)
            : Task.FromResult(true);

    public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) =>
        services.GetService<IManagedCliAutoUpdateStore>() is { } autoUpdateStore
            ? autoUpdateStore.SetAsync(cliName, enabled, cancellationToken)
            : Task.CompletedTask;

    public Task SetSessionStatusline(string paneId, string statusline) =>
        _ActOnSessionAsync(paneId, session => session.SetStatuslineAsync(statusline ?? string.Empty));

    public Task SetSessionName(string paneId, string name) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.CompletedTask
            : _ActOnSessionAsync(paneId, session => session.SetNameAsync(name));

    public Task SuggestSessionName(string paneId, string name) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.CompletedTask
            : _ActOnSessionAsync(paneId, session => session.SuggestNameAsync(name));

    public Task SendToSessionAsync(string paneId, string text) =>
        string.IsNullOrEmpty(text)
            ? Task.CompletedTask
            : _ActOnSessionAsync(paneId, session => session.InjectAndSubmitAsync(text));

    // A plugin or workflow may call from any thread, and the target may already be gone — a no-op then, never an
    // error. Decided under the launcher's exclusion (F1), acted on outside it, and looked up again right before
    // acting, so a pane that closed in between is left alone rather than written to (AC-1392).
    private async Task _ActOnSessionAsync(string paneId, Func<ISessionHandle, Task<bool>> act)
    {
        if (string.IsNullOrEmpty(paneId)
            || services.GetService<ISessionRegistry>() is not { } registry
            || services.GetService<ISessionLauncher>() is not { } launcher)
        {
            return;
        }

        if (await launcher.RunExclusiveAsync(() => registry.Find(paneId)).ConfigureAwait(false) is not { } decided
            || !ReferenceEquals(registry.Find(paneId), decided))
        {
            return;
        }

        await act(decided).ConfigureAwait(false);
    }

    // AC-1338: the same inbox a session's `notify cockpit-assistant` lands in, with this plugin as the stated sender —
    // the shape CiWatcher/SessionWatcher already use for a non-pane sender. The assistant is not in the registry's
    // panes (deliberately unwakeable through SendToSessionAsync), so this is a plugin's only door to it.
    public Task<bool> NotifyAssistantAsync(string kind, string body)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(body) || services.GetService<IAgentMessageInbox>() is not { } inbox)
        {
            return Task.FromResult(false);
        }

        // Bounded like CiWatcher's own deliveries, only wider: an evidence package (a diff-stat, a build tail) needs
        // room, but a plugin must not be able to pour a megabyte into the assistant's context in one message.
        var bounded = body.Length <= MaxAssistantNotifyLength
            ? body
            : body[..(MaxAssistantNotifyLength - AssistantNotifyTruncationMarker.Length)] + AssistantNotifyTruncationMarker;
        var delivery = inbox.Deliver($"cockpit-plugin:{pluginId}", AssistantIdentity.PaneId, kind.Trim(), bounded);
        return Task.FromResult(delivery.Outcome != AgentMessageDeliveryOutcome.RecipientInboxFull);
    }

    private const int MaxAssistantNotifyLength = 6_000;
    private const string AssistantNotifyTruncationMarker = " … (the rest of this message was cut off)";

    // The registry's snapshot read is safe from any thread, so this needs no hop; the binding reads it again on
    // every use rather than keeping anything of the pane.
    public IPluginSessionBinding BindToSession(string paneId) =>
        !string.IsNullOrEmpty(paneId) && services.GetService<ISessionRegistry>() is { } registry && registry.Find(paneId) is not null
            ? new PluginSessionBinding(paneId, registry, sessions, SendToSessionAsync)
            : new DetachedSessionBinding(paneId ?? string.Empty);

    public Task<PluginWorktreeInfo?> CreateRunWorktreeAsync(string repositoryDirectory, string? label, CancellationToken cancellationToken) =>
        CreateRunWorktreeAsync(repositoryDirectory, label, baseRef: null, cancellationToken);

    // AC-1337: forking from `baseRef`'s remote tip (an epic run's collection branch) instead of the checkout's own
    // branch when one is given. Null when there is no worktree manager or `repositoryDirectory` is no repository.
    public async Task<PluginWorktreeInfo?> CreateRunWorktreeAsync(string repositoryDirectory, string? label, string? baseRef, CancellationToken cancellationToken)
    {
        if (services.GetService<IWorktreeManager>() is not { } worktrees
            || string.IsNullOrWhiteSpace(repositoryDirectory)
            || await worktrees.DetectRepositoryAsync(repositoryDirectory, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var worktree = await worktrees.CreateForSessionAsync(Guid.NewGuid().ToString("N"), label, repositoryDirectory, baseRef: baseRef, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new PluginWorktreeInfo(worktree.Path, worktree.Branch);
    }

    public async Task<GitDirectoryStatus> DetectGitDirectoryStatusAsync(string directory, CancellationToken cancellationToken)
    {
        // No worktree manager (or no path) means the host cannot tell — Unknown, which the caller treats as needing
        // isolation, never as a licence to run free.
        if (string.IsNullOrWhiteSpace(directory) || services.GetService<IWorktreeManager>() is not { } worktrees)
        {
            return GitDirectoryStatus.Unknown;
        }

        // DetectRepositoryAsync returns null both for a true non-repository and for a probe failure on a real
        // one (dubious ownership, permission/lock error) — so "not a repository" is decided from the
        // filesystem (no .git), not from the probe failing, to avoid dropping isolation on a real checkout.
        var confirmedRepository = await worktrees.DetectRepositoryAsync(directory, cancellationToken).ConfigureAwait(false) is not null;
        return GitDirectoryStatusResolver.Resolve(directory, confirmedRepository);
    }

    public async Task<PluginRememberedWorkingPaths> GetRememberedWorkingPathsAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IWorkingPathHistoryStore>() is not { } store)
        {
            return PluginRememberedWorkingPaths.Empty;
        }

        var history = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new PluginRememberedWorkingPaths(history.Favorites, history.Recent);
    }

    public async Task RememberWorkingPathAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || services.GetService<IWorkingPathHistoryStore>() is not { } store)
        {
            return;
        }

        await store.RecordRecentAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    private ILogger? _Logger() => services.GetService<ILoggerFactory>()?.CreateLogger<PluginBackendHost>();

    // Once per plugin, not per call: one line says this plugin's window contributions are dropped, and why.
    private void _NoWindow(string member)
    {
        if (Interlocked.Exchange(ref _saidWindowless, 1) == 0)
        {
            _Logger()?.LogInformation(
                "Plugin {PluginId} called {Member}, and this backend has no window: its window contributions are ignored.",
                pluginId,
                member);
        }
    }
}
