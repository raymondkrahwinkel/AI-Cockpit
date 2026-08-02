using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Core.Mcp;
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

// The `ICockpitHost` a plugin receives in `ICockpitPlugin.Initialize`: the built
// service provider, the shared `ICockpitActions`, this plugin's own `IPluginStorage`
// slice, the contribution points routed to the running UI via an `IPluginContributionSink`, and
// the dialog helper. Built per plugin (each gets its own storage and its settings keyed by
// `pluginId`), so `Storage` and any settings view are scoped to this plugin.
//
// `ownPluginType`:
// The runtime type of the `ICockpitPlugin` instance this host was built for (AC-499), or
// `null` for a host built without one (most tests). The only thing it drives is
// `_OwnMcpServerContributions`: which of the container-wide `IPluginMcpProvider`
// registrations belong to *this* plugin rather than some other one. `services` is the whole app's
// shared provider — every plugin's `ConfigureServices` adds its own `IPluginMcpProvider` into
// the same collection — so there is no per-plugin scope to resolve against; type identity against the plugin's
// own concrete type is what stands in for it. This matches how the one plugin that needs it registers today
// (`services.AddSingleton&lt;IPluginMcpProvider&gt;(this)` — Depot), so its own provider's `GetType()`
// is literally the plugin's own type. A plugin that instead contributed a separate `IPluginMcpProvider`
// class would not match here and would need this check broadened, not the type swapped for a laxer one — see
// `CallMcpToolAsync`'s own remarks on why "any provider" is deliberately not the default any more.
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

    public void AddSettings(Func<Control> createView) =>
        contributionSink.AddPluginSettings(pluginId, pluginName, createView);

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

    public void ShowToast(string message, PluginToastSeverity severity, string? actionLabel, Action? onAction) =>
        services.GetRequiredService<IToastService>().Show(message, _ToToastSeverity(severity), actionLabel, onAction);

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

        // AC-577, no fast path — deliberately. A CheckAccess() shortcut would let this run inline on whatever
        // thread called, which is the branch that makes a test pass while proving nothing about the marshalling
        // it exists for. Without it the call hangs in a process with no dispatcher loop, and that is the honest
        // trade: CockpitHost is only ever constructed behind a running application, and a test that claims to
        // cover this line belongs in Cockpit.App.ViewTests (Cockpit.Core.Tests cannot even name Dispatcher now).
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

    // A plugin's dialog gets a gear in its title bar when the plugin has settings to open — asked for at the
    // moment the dialog opens rather than when the plugin was built, since a plugin registers its settings and
    // its dialogs in any order it likes. The dialog the operator is looking at is where they find out something
    // is unconfigured, so it is where the way to configure it belongs.
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

    // What comes through here is an issue body somebody else wrote, and rendering it is synchronous: a pipe-heavy
    // 65 KB description — the size a GitHub body is allowed to be — becomes tens of thousands of controls in an
    // all-Auto grid while the operator waits. The cap sits on this seam rather than inside MarkdownView because the
    // transcript renders through that same control, and cutting the cockpit's own output short would be a defect
    // rather than a guard (AC-303).
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
                    // Whatever models the profile's own provider offers. The host used to answer this from its own copy
                    // of the Claude aliases, which is how Autopilot came to describe that list as running cheapest-first
                    // while it ran the other way round (AC-256): a second copy carrying meaning nobody promised it. Ask
                    // the provider instead, so every provider is served by the same code and none needs the host to know
                    // its vocabulary. The catalogue stays only as the answer when there is no registration to ask —
                    // a Claude profile whose provider plugin did not load, and the legacy typed config that
                    // ProviderConfigEntry.ToDomain migrates away on read. Keeping it there leaves that case exactly as
                    // it behaves today rather than quietly emptying it as part of this change.
                    ModelSuggestions = model?.Choices ?? (profile.Claude is not null ? SessionOptionCatalog.ClaudeModelSuggestions : []),
                    // Cost is the provider's own estimate or nothing at all; the host never ranks or prices a model.
                    ModelCostEstimatesCheapestFirst = model?.CostEstimatesCheapestFirst ?? [],
                    // The local, free-to-run providers; everything else (Claude, Codex, hosted plugin providers) is a paid API.
                    RunsLocally = profile.Provider is Core.Profiles.SessionProvider.Ollama or Core.Profiles.SessionProvider.LmStudio,
                };
            })
            .ToList();
    }

    // The model launch option the profile's own session provider declares, or `null` when the profile
    // is not plugin-backed or its provider offers no model choice. Found by `WellKnownPluginSessionOptions.Model`
    // — the same key the driver adapter bridges a live model switch through — so the host locates it without knowing
    // any provider's option vocabulary. Only the statically declared options are read: `SessionProviderRegistration.ResolveOptionsAsync`
    // reaches out to a CLI, and this runs on every plan emission and step start, where a stall would be felt.
    private static PluginSessionLaunchOption? _DeclaredModelOption(IPluginProviderRegistry registry, Core.Profiles.SessionProfile profile) =>
        profile.ProviderConfig is Core.Profiles.PluginProviderConfig plugin
            ? registry.Resolve(plugin.ProviderId)?.Options.FirstOrDefault(option => option.Key == WellKnownPluginSessionOptions.Model)
            : null;

    // Idempotent upsert-by-name into the shared `IMcpServerStore` registry (#60). No entry named
    // `McpServerContribution.Name` yet → add one (enabled by default, scoped as requested). An
    // entry already exists → refresh only the plugin-owned `McpServerConfig.Url`/
    // `McpServerConfig.Auth`/`McpServerConfig.ApiKey`, leaving
    // `McpServerConfig.Enabled` and `McpServerConfig.Scope` untouched — respects a
    // server the user disabled or rescoped from the MCP-servers dialog instead of clobbering their choice on
    // every plugin restart/settings-save. Deliberately does *not* track "the user deleted this on
    // purpose": a removed entry is indistinguishable from one never registered, so it comes back as a fresh
    // (enabled) add the next time the plugin calls this — bounded to the plugin's own trigger points
    // (`Initialize`, its settings-saved callback), not a background loop, so it is a re-add on explicit
    // action rather than a silent fight with the user.
    //
    // Called fire-and-forget from a synchronous callback (per the interface doc), so a store I/O failure here
    // would otherwise throw on an unobserved task — invisible to the plugin and the operator (#184). Caught and
    // attributed to this plugin in `PluginDiagnostics`; a cancellation (app shutting down) is not
    // this plugin's fault and is left unrecorded, and resolving `IMcpServerStore` itself stays
    // outside the catch — a missing registration is a host bug, not something to blame on the plugin.
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
                // Refresh only the connection fields; the entry's Scope and Enabled are the operator's and are left as
                // they are (a server they disabled or rescoped in the dialog stays that way). Reuses ToServerConfig
                // rather than restating its auth-field-clearing rule (AC-500) — the one place that sees both DTOs is
                // exactly where that rule should live, per this file's own doc comment on PluginMcpMapping.
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

    // Looks up the OAuth server contributed under `name` — the shared registry first, then (AC-504)
    // every registered `IPluginMcpProvider`'s own `_ResolveOAuthServerAsync` fallback, for a
    // plugin (Depot) whose servers are delivered to sessions per-project rather than pushed into that registry — and
    // asks the shared `IMcpOAuthCoordinator` non-interactively (AC-243), the same read the host's own
    // MCP-servers dialog does per row. A name nothing resolves to (never contributed, contributed as a static
    // token, or removed), or a coordinator that is not registered, answers `PluginMcpAuthState.Unknown`
    // rather than throwing — a status read is informational only.
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

    // Drives the same interactive loopback sign-in the host's own MCP-servers dialog offers (AC-243/AC-355) for
    // the OAuth server this plugin contributed under `name`, reporting only a named outcome —
    // never a token (Iron Law #8) and never the failure detail the dialog's own row reserves for its own log line.
    // A name with no matching OAuth entry, or a host with no coordinator registered, answers
    // `PluginMcpSignInOutcome.Unavailable` without attempting anything.
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

    // Calls a tool on this plugin's own MCP server through the same `IMcpToolInvoker` a session's
    // tool-loop uses to reach it (AC-502) — on the app's behalf, never opening a browser and never handing the
    // plugin the bearer token the invoker used to authenticate the call.
    //
    // Refuses any `name` that resolves to neither the shared registry nor any plugin's own
    // `IPluginMcpProvider.GetMcpServers()` — the same `_ResolveOAuthServerAsync` lookup
    // `GetMcpServerAuthStateAsync`/`SignInMcpServerAsync` already use (AC-504), so a plugin
    // whose servers are delivered per-project (Depot, since AC-504) rather than pushed via `AddMcpServer`
    // is reachable here the same way its own sign-in already is. What this still excludes: a cockpit-internal
    // endpoint (terminal, worktrees, the delegation orchestrator, …) mounted via `AddMcpEndpoint` —
    // those carry no plugin's consent and go through no permission gate a session's own connect applies, and
    // neither the registry nor `IPluginMcpProvider` ever lists them.
    //
    // AC-499: *this* check (whether the name is known at all) still accepts any plugin's provider, unchanged
    // from AC-504 — but the invoker's own resolution used to be stricter, only the project-scoped catalog, so a
    // server this plugin delivers per-project (Depot) could pass this check and still fail to resolve inside
    // `IMcpToolInvoker.InvokeAsync` whenever the calling project had not yet been saved with the row
    // that would put it in that catalog. `_OwnMcpServerContributions` closes that gap by handing the
    // invoker a caller-scoped fallback list — deliberately narrower than this accept check: only *this*
    // plugin's own contributions, never another plugin's, because a project-agnostic fallback that let any caller
    // reach any plugin's server would turn "not saved yet" into "no scoping at all". This accept check staying lax
    // is pre-existing AC-504 behaviour and out of this fix's scope, not a second inconsistency introduced here.
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
                _ => PluginMcpToolCallResult.Failed(result.Error ?? "The tool call failed."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Record(pluginId, pluginName, "mcp-tool-call", exception.Message);
            return PluginMcpToolCallResult.Failed(exception.Message);
        }
    }

    // The OAuth server named `name`, wherever it lives: the shared registry first (a
    // registry-configured server, or a plugin still on the AC-243 push model), then, if nothing there matches
    // (AC-504), every `IPluginMcpProvider`'s own project-agnostic `IPluginMcpProvider.GetMcpServers()`
    // — a plugin whose servers are delivered to sessions per-project (Depot, one server per connection) still has
    // no project to scope by here: signing in happens from that plugin's own settings view, not from inside a
    // session. A plugin that throws while listing its servers is treated the same way `McpServerCatalog`
    // treats it — logged and skipped, not fatal to the lookup.
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

    // Whether `name` resolves to anything at all — the shared registry (any auth kind, not only
    // OAuth, unlike `_ResolveOAuthServerAsync`) or any plugin's own `IPluginMcpProvider.GetMcpServers()`
    // (AC-502 review). This is `CallMcpToolAsync`'s own scope check: it deliberately does not
    // distinguish "this calling plugin's own server" from "some other plugin's" — the same laxness
    // `_ResolveOAuthServerAsync` already accepts for a sign-in — because there is no way from inside a
    // shared-container-resolved `IPluginMcpProvider` list to tell whose instance is whose. What matters
    // is that a cockpit-internal endpoint (never in the registry, never behind `IPluginMcpProvider`)
    // can never pass this check.
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

    // This plugin's own MCP servers, project-agnostic (the same `IPluginMcpProvider.GetMcpServers()`
    // overload `_ResolveOAuthServerAsync` already falls back to), mapped to `McpServerConfig`
    // and handed to `IMcpToolInvoker.InvokeAsync`/`IMcpToolProbe.ProbeAsync` as their own
    // additive fallback candidate list (AC-499). Scoped by `ownPluginType` (see this class's own
    // parameter doc) to *only* the `IPluginMcpProvider` instance(s) whose concrete type is this
    // plugin's own — never the whole container-wide `services.GetServices&lt;IPluginMcpProvider&gt;()` set —
    // so this method can never hand a caller a fallback into some other plugin's server. Empty when no plugin
    // instance was supplied (most tests) or when this plugin registers no matching provider.
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

    // Delegates to `IMcpToolProbe` (AC-503) and maps its Core-level `McpToolProbeResult`
    // onto the plugin-facing `McpProbeResult` — the same isolation seam `GetMcpServerAuthStateAsync`
    // and `SignInMcpServerAsync` already keep between `Cockpit.Core`'s own vocabulary and the
    // plugin SDK's. A host with no probe registered (a test fake, an older host) answers `McpProbeResult.Failed`
    // without attempting anything.
    //
    // AC-499: also hands the probe `_OwnMcpServerContributions` as its caller-scoped fallback — this
    // call takes no project id, so a plugin whose servers never land in the shared registry (Depot, AC-504) would
    // otherwise be unprobeable regardless of whether the project row that would put it in the catalog exists yet.
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

    // Opens the cockpit's New-session dialog (#AC-96) pre-filled from `prefill`, on the UI thread,
    // and — once the operator confirms — reports the started session's pane id through `onStarted`,
    // or fires `onCancelled` when they dismiss it or no session started. Exactly one callback runs.
    // Routed through `CockpitViewModel` so the session is minted by the app's own launch path (worktree
    // isolation, the launch-result recorded for Duplicate) rather than a second, divergent one; a host with no cockpit
    // view model reports cancellation, never a silent nothing.
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

        // DetectRepositoryAsync returns null both for a real not-a-repository and for a probe that merely failed (git
        // refusing a real repo it will not read — dubious ownership, a permission or lock error, no commit yet). Feeding
        // that null straight to NotARepository would drop isolation for a real checkout, so the resolver decides
        // "not a repository" from the filesystem (no .git in the tree), not from the probe failing — fail-closed.
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

        // AC-577, fast path — deliberately, and the third of this file's three dispatcher sites to choose
        // differently from the other two. This one is called on the UI thread most of the time and marshals a
        // field write rather than a dialog, so the inline branch is what it costs nothing to take. The price is
        // named where it is paid: a test that takes this branch proves nothing about the marshalling, so a test
        // covering it belongs in Cockpit.App.ViewTests, where the dispatcher is real and CheckAccess is honest.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(Apply).GetTask();
    }

    // Maps by name, not ordinal — same reasoning as _ToServerScope below.
    private static ToastSeverity _ToToastSeverity(PluginToastSeverity severity) => severity switch
    {
        PluginToastSeverity.Success => ToastSeverity.Success,
        PluginToastSeverity.Warning => ToastSeverity.Warning,
        PluginToastSeverity.Error => ToastSeverity.Error,
        _ => ToastSeverity.Information,
    };

}
