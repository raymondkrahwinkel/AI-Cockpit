using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Controls;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.ManagedCli;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workflows;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// The `ICockpitHost` a plugin receives in `ICockpitPlugin.Initialize`, scoped by `pluginId`.
// AC-499: `ownPluginType` (this instance's runtime type, or `null` in tests) scopes
// `_OwnMcpServerContributions` to this plugin's own `IPluginMcpProvider` registration.
internal sealed class CockpitHost(
    string pluginId,
    string pluginName,
    IServiceProvider services,
    IPluginContributionSink contributionSink,
    ICockpitActions actions,
    IPluginStorage storage,
    IPluginDialogHost dialogHost,
    ICockpitSessionObserver sessions,
    PluginDiagnostics diagnostics,
    IReadOnlyList<string>? declaredSecretKeys = null,
    Type? ownPluginType = null) : ICockpitHost
{
    public IServiceProvider Services => services;

    public ICockpitActions Actions => actions;

    public IPluginStorage Storage => storage;

    public ICockpitSessionObserver Sessions => sessions;

    // AC-128: the transport-verified pane behind the current in-process MCP call, read from the ambient request
    // context the auth middleware set. A plugin's own MCP tool keys on this so it acts on the calling session, not a
    // session id the agent named. Null off the verified path (no MCP call in flight).
    public string? CurrentMcpCallerPaneId => McpRequestContext.CurrentPaneId;

    // This plugin's open channels, keyed by the id it named them (AC-1023). Kept so re-opening one replaces it
    // rather than leaving a second gateway subscribed to the same transcript, doubling every relayed row.
    private readonly Dictionary<string, IAssistantChannelGateway> _assistantChannels = new(StringComparer.Ordinal);

    public void AddSettings(Func<Control> createView) =>
        contributionSink.AddPluginSettings(pluginId, pluginName, createView);

    public void AddSettings(Func<Control> createView, string category) =>
        contributionSink.AddPluginSettings(pluginId, pluginName, createView, category);

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
            assistantHost,
            services.GetRequiredService<IConsentBroker>(),
            services.GetRequiredService<ILogger<AssistantChannelGateway>>());
        _assistantChannels[contribution.Id] = gateway;

        return gateway;
    }

    public bool HasSettings => contributionSink.HasPluginSettings(pluginId);

    public Task ShowSettingsAsync() => contributionSink.OpenPluginSettingsAsync(pluginId);

    public void AddSideMenuButton(string title, Action onInvoke) =>
        contributionSink.AddPluginSideButton(pluginId, title, onInvoke);

    public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke)
    {
        var badge = new SideMenuButtonBadge();
        contributionSink.AddPluginSideButton(pluginId, title, onInvoke, badge);
        return badge;
    }

    public void AddShortcut(PluginShortcut shortcut) =>
        contributionSink.AddPluginShortcut(shortcut);

    public void ShowToast(string message, PluginToastSeverity severity, string? actionLabel, Action? onAction)
    {
        // AC-1074: a toast is gone in seconds, and an error nobody was looking at is exactly what the log is for.
        if (severity is PluginToastSeverity.Error)
        {
            services.GetService<ILogger<CockpitHost>>()?.LogError("Plugin {PluginId}: {Message}", pluginId, message);
        }

        services.GetRequiredService<IToastService>().Show(message, _ToToastSeverity(severity), actionLabel, onAction);
    }

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) =>
        // The plugin's identity is stamped here, not taken from the request — a plugin cannot ask under another's name.
        services.GetRequiredService<IConsentBroker>()
            .RequestConsentAsync(request with { Source = request.Source with { PluginId = pluginId } });

    public void AddSideMenuSection(string title, Func<Control> createView) =>
        contributionSink.AddPluginSideSection(pluginId, title, createView);

    public void AddSessionHeaderAction(PluginSessionAction action) =>
        contributionSink.AddPluginSessionHeaderAction(action);

    public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView) =>
        contributionSink.AddPluginSessionHeaderItem(createView);

    public void AddSessionBanner(Func<IPluginSessionContext, Control> createView) =>
        contributionSink.AddPluginSessionBannerItem(createView);

    public void AddSupervisedActivityProvider(ISupervisedActivitySource source) =>
        contributionSink.AddSupervisedActivityProvider(source);

    public void AddToolbarAction(ToolbarAction action) =>
        contributionSink.AddToolbarAction(pluginId, action);

    public void AddConversationPicker(ConversationPickerRegistration picker) =>
        services.GetRequiredService<IConversationPickerRegistry>().Register(picker);

    // This plugin's own storage, observe surface and declared secret keys travel with the registration: a placed
    // instance builds its context long after load, and by then the widget id is the only thing linking it back
    // here. The declared keys are what lets an export drop a credential the name rule cannot guess ("pat").
    public void AddWidget(WidgetRegistration registration)
    {
        // Refused means another plugin already contributes this type id. Logged rather than thrown: a plugin
        // cannot know what else is installed, and taking the cockpit down over a name clash is a worse answer
        // than the widget being the one that was already there.
        if (!services.GetRequiredService<IWidgetRegistry>().Register(registration, storage, sessions, declaredSecretKeys ?? []))
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogWarning(
                "Widget type '{WidgetId}' is already contributed by another plugin; this registration is ignored",
                registration.Id);
        }
    }

    public IReadOnlyList<WidgetRegistration> Widgets =>
        services.GetRequiredService<IWidgetRegistry>().Widgets;

    // Unlike AddWidget, no storage/sessions travel with this registration: a dock panel's view factory takes no
    // context, so a plugin that needs per-instance state builds its own IWidgetContext from what host.Storage and
    // host.Sessions already give it.
    public void AddDockPanel(DockPanelRegistration registration)
    {
        if (!services.GetRequiredService<IDockPanelRegistry>().Register(registration))
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogWarning(
                "Dock panel '{DockPanelId}' is already contributed by another plugin; this registration is ignored",
                registration.Id);
        }
    }

    // This plugin's own storage and observe surface travel with the registration, the same way a widget's do: a
    // workspace of this type builds its context long after load, and by then the type id is the only thing linking
    // it back here.
    public void AddWorkspaceType(WorkspaceTypeRegistration registration)
    {
        // Refused means another plugin already contributes this type id. Logged rather than thrown: a plugin cannot
        // know what else is installed, and taking the cockpit down over a name clash is a worse answer than the
        // type being the one that was already there.
        if (!services.GetRequiredService<IWorkspaceTypeRegistry>().Register(registration, storage, sessions))
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogWarning(
                "Workspace type '{WorkspaceTypeId}' is already contributed by another plugin; this registration is ignored",
                registration.Id);
        }
    }

    public IReadOnlyList<WorkspaceTypeRegistration> WorkspaceTypes =>
        services.GetRequiredService<IWorkspaceTypeRegistry>().WorkspaceTypes;

    // The programmatic "+" for a plugin's own workspace type: a plugin that received an intent surfaces its
    // workspace so the operator lands on it. Marshalled to the UI thread since a plugin may dispatch from any
    // thread; a design-time/headless host (no view model resolved) simply does nothing.
    public async Task OpenWorkspaceAsync(string workspaceTypeId)
    {
        // The plugin's workspaces live on the on-screen view model — the same instance the UI binds to — not a
        // separately DI-resolved WorkspacesViewModel, which would open the workspace on a surface no one is looking at.
        if (services.GetService<CockpitViewModel>()?.Workspaces is not { } workspaces)
        {
            return;
        }

        // AC-577: no CheckAccess() fast path, deliberately — that shortcut would let a test pass inline
        // without proving anything about marshalling; a test for this belongs in Cockpit.App.ViewTests.
        await Dispatcher.UIThread.InvokeAsync(() => workspaces.OpenWorkspaceAsync(workspaceTypeId));
    }

    public void AddProjectField(ProjectFieldRegistration registration)
    {
        // Refused means another plugin already registered this key. That is the agreed case, not a mistake — the
        // GitHub Issues and Pull Requests plugins both offer "which repository" so either one alone still shows the
        // field — so this is logged at debug level, unlike the widget/workspace clashes above.
        if (!services.GetRequiredService<IProjectFieldRegistry>().Register(registration))
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogDebug(
                "Project field '{ProjectFieldKey}' is already contributed; this registration is ignored",
                registration.Key);
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
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogDebug(
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
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogDebug(
                "Memory source '{MemorySourceScheme}' is already contributed; this registration is ignored",
                registration.Scheme);
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
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogDebug(
                "Memory source family '{MemorySourceFamilyKey}' is already declared; this registration is ignored",
                family.Key);
        }
    }

    public void AddSharedProjectSource(ISharedProjectSource source)
    {
        // Refused means another plugin already contributes this key — agreement, not a clash, the same reason
        // AddProjectMemorySource logs at debug rather than warning.
        if (!services.GetRequiredService<ISharedProjectSourceRegistry>().Register(source))
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogDebug(
                "Shared-project source '{SharedProjectSourceKey}' is already contributed; this registration is ignored",
                source.Key);
        }
    }

    public void RemoveSharedProjectSource(string key) =>
        services.GetRequiredService<ISharedProjectSourceRegistry>().Remove(key);

    public IReadOnlyList<ISharedProjectSource> SharedProjectSources =>
        services.GetRequiredService<ISharedProjectSourceRegistry>().Sources;

    public async Task<string?> GetProjectFieldValueAsync(string key, string? paneId, CancellationToken cancellationToken)
    {
        // No pane named and none selected means there is no project to read from — not an error, just nothing to say.
        var pane = string.IsNullOrEmpty(paneId) ? sessions.ActivePaneId : paneId;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(pane))
        {
            return null;
        }

        // Which project that pane belongs to is one question with one answer (AC-320), asked here rather than
        // looked up again.
        var projectId = await services.GetRequiredService<ISessionProjectResolver>().ProjectIdOfAsync(pane, cancellationToken);
        if (string.IsNullOrEmpty(projectId))
        {
            return null;
        }

        var projects = await services.GetRequiredService<IProjectStore>().LoadAsync(cancellationToken);
        return projects.Find(projectId)?.LinkedAs(key);
    }

    public async Task<IReadOnlyList<string>> GetProjectFieldValuesAsync(string key, string? paneId, CancellationToken cancellationToken)
    {
        var pane = string.IsNullOrEmpty(paneId) ? sessions.ActivePaneId : paneId;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(pane))
        {
            return [];
        }

        var projectId = await services.GetRequiredService<ISessionProjectResolver>().ProjectIdOfAsync(pane, cancellationToken);
        if (string.IsNullOrEmpty(projectId))
        {
            return [];
        }

        var projects = await services.GetRequiredService<IProjectStore>().LoadAsync(cancellationToken);
        return projects.Find(projectId)?.LinkedAsAll(key) ?? [];
    }

    public async Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId, CancellationToken cancellationToken)
    {
        var pane = string.IsNullOrEmpty(paneId) ? sessions.ActivePaneId : paneId;
        if (string.IsNullOrEmpty(pane))
        {
            return [];
        }

        var projectId = await services.GetRequiredService<ISessionProjectResolver>().ProjectIdOfAsync(pane, cancellationToken);
        if (string.IsNullOrEmpty(projectId))
        {
            return [];
        }

        var projects = await services.GetRequiredService<IProjectStore>().LoadAsync(cancellationToken);
        var resources = projects.Find(projectId)?.Resources ?? [];
        return [.. resources
            .Where(resource => resource.Role == ProjectResourceRole.Memory)
            .Select(resource => new ProjectMemoryRow(resource.Reference, resource.Label, resource.ReachesSessions))];
    }

    public void AddTrackerProvider(ITrackerProvider provider)
    {
        // First registration for a tracker id wins; a later one is logged and ignored rather than added beside it.
        if (!services.GetRequiredService<ITrackerProviderRegistry>().Register(provider))
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogWarning(
                "Tracker '{TrackerId}' is already contributed by another plugin; this registration is ignored",
                provider.TrackerId);
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
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogDebug(
                "Session-resource provider {Provider} is already registered; this registration is ignored",
                provider.GetType().Name);
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

    // A plugin's dialog gets a gear in its title bar when the plugin has settings to open — checked when the
    // dialog opens, not when the plugin is built, since settings and dialogs can register in any order.
    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        _ShowPluginDialogAsync(title, createContent, width, height, singleInstanceKey: null);

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        // Scoped to the plugin, so a plugin only has to be unique within itself: two plugins picking "issues"
        // are two windows, which is what they are. Without the scope the first plugin to open one would answer
        // for the other, and the operator would act on the wrong repository.
        _ShowPluginDialogAsync(title, createContent, width, height, $"{pluginId}:{singleInstanceKey}");

    private Task _ShowPluginDialogAsync(string title, Func<Control> createContent, double width, double height, string? singleInstanceKey) =>
        dialogHost.ShowDialogAsync(
            title,
            createContent,
            width,
            height,
            onOpenSettings: contributionSink.HasPluginSettings(pluginId)
                ? () => contributionSink.OpenPluginSettingsAsync(pluginId)
                : null,
            singleInstanceKey: singleInstanceKey);

    // Delegates to the cockpit's own `MarkdownView` — no second parser, one markdown idiom for both the transcript and every plugin dialog.
    public Control CreateMarkdownView(string markdown) => new MarkdownView { Markdown = _CapForRendering(markdown) };

    // AC-303: caps a plugin-supplied markdown body (e.g. a 65 KB GitHub issue) before synchronous rendering,
    // which otherwise stalls the UI in an all-Auto grid; capped here, not in MarkdownView, since the transcript
    // also renders through that control and must not be truncated.
    private const int MaxPluginMarkdownCharacters = 64 * 1024;

    // Nullable although the contract says otherwise: the caller is plugin code, which may well be compiled with
    // nullable disabled, and passing null here used to render an empty view rather than throw. Dereferencing it
    // would move that into an exception on the host's UI thread — the very shape AC-304 is about.
    private static string? _CapForRendering(string? markdown) =>
        markdown is { Length: > MaxPluginMarkdownCharacters }
            ? string.Concat(
                markdown.AsSpan(0, MaxPluginMarkdownCharacters),
                "\n\n*(truncated — open the item in its tracker to read the rest)*")
            : markdown;

    public void OnSettingsSaved(Action callback) =>
        contributionSink.AddSettingsSavedHandler(pluginId, callback);

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
                return new PluginProfileInfo(profile.Label, profile.Provider.ToString(), profile.Claude?.ConfigDir ?? string.Empty)
                {
                    // AC-256: asks the provider for its models instead of keeping the host's own copy of the
                    // Claude aliases, which had drifted out of cheapest-first order. Falls back to the catalogue
                    // only when there is no registration to ask (unloaded provider plugin, legacy typed config).
                    ModelSuggestions = model?.Choices ?? (profile.Claude is not null ? SessionOptionCatalog.ClaudeModelSuggestions : []),
                    // Cost is the provider's own estimate or nothing at all; the host never ranks or prices a model.
                    ModelCostEstimatesCheapestFirst = model?.CostEstimatesCheapestFirst ?? [],
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
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogWarning(
                exception, "Plugin {PluginId}'s MCP server contribution '{ServerName}' failed to register.", pluginId, contribution.Name);
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
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogWarning(
                exception, "Plugin {PluginId}'s MCP server removal ('{ServerName}') failed.", pluginId, name);
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
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>().LogWarning(
                exception, "A plugin failed to list its MCP servers while resolving an OAuth sign-in; leaving them out of the lookup.");
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

    // Opens the New-session dialog (#AC-96) pre-filled from `prefill`; exactly one callback runs, `onStarted`
    // or `onCancelled`. Routed through `CockpitViewModel` so the session is minted by the app's own launch
    // path (worktree isolation, Duplicate's launch-result), not a second, divergent one.
    public async Task ShowNewSessionDialogAsync(
        NewSessionPrefill? prefill = null,
        Action<string>? onStarted = null,
        Action? onCancelled = null)
    {
        string? paneId;
        try
        {
            // AC-577, no fast path — deliberately, and here it is not even a trade: what this marshals to is a
            // modal dialog, which has nowhere to appear in a process without a dispatcher loop. An inline branch
            // would turn "no UI thread" from a hang into a dialog nobody can answer.
            paneId = await Dispatcher.UIThread.InvokeAsync(() =>
                services.GetService<CockpitViewModel>() is { } cockpit
                    ? cockpit.ShowNewSessionDialogForPluginAsync(prefill)
                    : Task.FromResult<string?>(null));
        }
        catch (Exception ex)
        {
            // The exactly-one-callback contract has to hold even when the dialog or the launch throws: a plugin that
            // bridges these callbacks to a TaskCompletionSource would otherwise wait forever. A failure is "nothing
            // started" — log it and fall through to onCancelled rather than letting the exception drop both callbacks.
            services.GetService<ILoggerFactory>()?.CreateLogger<CockpitHost>()
                .LogError(ex, "Opening the New-session dialog for plugin '{PluginId}' failed", pluginId);
            paneId = null;
        }

        if (paneId is not null)
        {
            onStarted?.Invoke(paneId);
        }
        else
        {
            onCancelled?.Invoke();
        }
    }

    public Task SetSessionStatusline(string paneId, string statusline) =>
        _MutateSessionAsync(paneId, session => session.Statusline = statusline ?? string.Empty);

    public Task SetSessionName(string paneId, string name) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.CompletedTask
            : _MutateSessionAsync(paneId, session => session.SetNameDirectly(name));

    public Task SuggestSessionName(string paneId, string name) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.CompletedTask
            : _MutateSessionAsync(paneId, session => session.SuggestName(name));

    public Task SendToSessionAsync(string paneId, string text) =>
        string.IsNullOrEmpty(text)
            ? Task.CompletedTask
            : _MutateSessionAsync(paneId, session => session.InjectAndSubmit(text));

    public IPluginSessionBinding BindToSession(string paneId)
    {
        if (string.IsNullOrEmpty(paneId) || services.GetService<CockpitViewModel>() is not { } cockpit)
        {
            return new DetachedSessionBinding(paneId ?? string.Empty);
        }

        // FindSession walks the session collections, which only the UI thread may do while panes come and go.
        bool IsLive() => cockpit.FindSession(paneId) is not null;

        return (Dispatcher.UIThread.CheckAccess() ? IsLive() : Dispatcher.UIThread.Invoke(IsLive))
            ? new CockpitSessionBinding(paneId, cockpit, sessions, SendToSessionAsync)
            : new DetachedSessionBinding(paneId);
    }

    public Task<Cockpit.Plugins.Abstractions.Workspaces.PluginWorktreeInfo?> CreateRunWorktreeAsync(string repositoryDirectory, string? label, System.Threading.CancellationToken cancellationToken) =>
        services.GetService<CockpitViewModel>() is { } cockpit
            ? cockpit.CreateRunWorktreeAsync(repositoryDirectory, label, cancellationToken)
            : Task.FromResult<Cockpit.Plugins.Abstractions.Workspaces.PluginWorktreeInfo?>(null);

    public async Task<Cockpit.Plugins.Abstractions.Workspaces.GitDirectoryStatus> DetectGitDirectoryStatusAsync(string directory, System.Threading.CancellationToken cancellationToken)
    {
        // No worktree manager (or no path) means the host cannot tell — Unknown, which the caller treats as needing
        // isolation, never as a licence to run free.
        if (string.IsNullOrWhiteSpace(directory) || services.GetService<IWorktreeManager>() is not { } worktrees)
        {
            return Cockpit.Plugins.Abstractions.Workspaces.GitDirectoryStatus.Unknown;
        }

        // DetectRepositoryAsync returns null both for a true non-repository and for a probe failure on a real
        // one (dubious ownership, permission/lock error) — so "not a repository" is decided from the
        // filesystem (no .git), not from the probe failing, to avoid dropping isolation on a real checkout.
        var confirmedRepository = await worktrees.DetectRepositoryAsync(directory, cancellationToken).ConfigureAwait(false) is not null;
        return GitDirectoryStatusResolver.Resolve(directory, confirmedRepository);
    }

    public async Task<Cockpit.Plugins.Abstractions.Workspaces.PluginRememberedWorkingPaths> GetRememberedWorkingPathsAsync(System.Threading.CancellationToken cancellationToken)
    {
        if (services.GetService<IWorkingPathHistoryStore>() is not { } store)
        {
            return Cockpit.Plugins.Abstractions.Workspaces.PluginRememberedWorkingPaths.Empty;
        }

        var history = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Cockpit.Plugins.Abstractions.Workspaces.PluginRememberedWorkingPaths(history.Favorites, history.Recent);
    }

    public async Task RememberWorkingPathAsync(string directory, System.Threading.CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || services.GetService<IWorkingPathHistoryStore>() is not { } store)
        {
            return;
        }

        await store.RecordRecentAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    // Find the session pane by its id and mutate it on the UI thread. A plugin or workflow may call from any
    // thread, and the target may already be gone (a closed session) — a no-op then, never an error.
    private Task _MutateSessionAsync(string paneId, Action<SessionPanelViewModel> mutate)
    {
        if (string.IsNullOrEmpty(paneId) || services.GetService<CockpitViewModel>() is not { } cockpit)
        {
            return Task.CompletedTask;
        }

        void Apply()
        {
            if (cockpit.FindSession(paneId) is { } target)
            {
                mutate(target);
            }
        }

        // AC-577: fast path here, unlike this file's other two dispatcher sites — usually called on the UI
        // thread and marshals a field write rather than a dialog, so the inline CheckAccess() branch costs
        // nothing; a test covering that branch belongs in Cockpit.App.ViewTests, not here.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(Apply).GetTask();
    }

    // AC-1033. `article` is resolved against this plugin's own branch first, then as written, so a plugin
    // names its own page with the id it gave the file and can still point at one of ours — without repeating
    // its own id, which would be a second place its name is written down.
    public Control CreateHelpHint(string article, string? section = null, string? label = null) =>
        _Help() is { } help
            ? new HelpHint(help, help.Resolve(pluginId, article, section), label, $"a “?” in {pluginName}")
            : new Panel { IsVisible = false };

    public void OpenHelp(string article, string? section = null) =>
        _Help()?.Open(_Help()!.Resolve(pluginId, article, section), $"a link in {pluginName}");

    public bool HasHelp(string article, string? section = null) =>
        _Help() is { } help && help.Contains(help.Resolve(pluginId, article, section));

    private HelpService? _Help() => services.GetService<HelpService>();

    // Maps by name, not ordinal — same reasoning as _ToServerScope below.
    private static ToastSeverity _ToToastSeverity(PluginToastSeverity severity) => severity switch
    {
        PluginToastSeverity.Success => ToastSeverity.Success,
        PluginToastSeverity.Warning => ToastSeverity.Warning,
        PluginToastSeverity.Error => ToastSeverity.Error,
        _ => ToastSeverity.Information,
    };

}
