using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Mcp;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workflows;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

/// <summary>
/// The <see cref="ICockpitHost"/> a plugin receives in <see cref="ICockpitPlugin.Initialize"/>: the built
/// service provider, the shared <see cref="ICockpitActions"/>, this plugin's own <see cref="IPluginStorage"/>
/// slice, the contribution points routed to the running UI via an <see cref="IPluginContributionSink"/>, and
/// the dialog helper. Built per plugin (each gets its own storage and its settings keyed by
/// <paramref name="pluginId"/>), so <see cref="Storage"/> and any settings view are scoped to this plugin.
/// </summary>
internal sealed class CockpitHost(
    string pluginId,
    string pluginName,
    IServiceProvider services,
    IPluginContributionSink contributionSink,
    ICockpitActions actions,
    IPluginStorage storage,
    IPluginDialogHost dialogHost,
    ICockpitSessionObserver sessions,
    IReadOnlyList<string>? declaredSecretKeys = null) : ICockpitHost
{
    public IServiceProvider Services => services;

    public ICockpitActions Actions => actions;

    public IPluginStorage Storage => storage;

    public ICockpitSessionObserver Sessions => sessions;

    public void AddSettings(Func<Control> createView) =>
        contributionSink.AddPluginSettings(pluginId, pluginName, createView);

    public bool HasSettings => contributionSink.HasPluginSettings(pluginId);

    public Task ShowSettingsAsync() => contributionSink.OpenPluginSettingsAsync(pluginId);

    public void AddSideMenuButton(string title, Action onInvoke) =>
        contributionSink.AddPluginSideButton(pluginId, title, onInvoke);

    public void AddShortcut(PluginShortcut shortcut) =>
        contributionSink.AddPluginShortcut(shortcut);

    public void ShowToast(string message, PluginToastSeverity severity, string? actionLabel, Action? onAction) =>
        services.GetRequiredService<IToastService>().Show(message, _ToToastSeverity(severity), actionLabel, onAction);

    public void AddSideMenuSection(string title, Func<Control> createView) =>
        contributionSink.AddPluginSideSection(pluginId, title, createView);

    public void AddSessionHeaderAction(PluginSessionAction action) =>
        contributionSink.AddPluginSessionHeaderAction(action);

    public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView) =>
        contributionSink.AddPluginSessionHeaderItem(createView);

    public void AddConversationPicker(ConversationPickerRegistration picker) =>
        services.GetRequiredService<IConversationPickerRegistry>().Register(picker);

    // This plugin's own storage, observe surface and declared secret keys travel with the registration: a placed
    // instance builds its context long after load, and by then the widget id is the only thing linking it back
    // here. The declared keys are what lets an export drop a credential the name rule cannot guess ("pat").
    public void AddWidget(WidgetRegistration registration) =>
        services.GetRequiredService<IWidgetRegistry>().Register(registration, storage, sessions, declaredSecretKeys ?? []);

    public IReadOnlyList<WidgetRegistration> Widgets =>
        services.GetRequiredService<IWidgetRegistry>().Widgets;

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

    /// <summary>
    /// A plugin's dialog gets a gear in its title bar when the plugin has settings to open — asked for at the
    /// moment the dialog opens rather than when the plugin was built, since a plugin registers its settings and
    /// its dialogs in any order it likes. The dialog the operator is looking at is where they find out something
    /// is unconfigured, so it is where the way to configure it belongs.
    /// </summary>
    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        dialogHost.ShowDialogAsync(
            title,
            createContent,
            width,
            height,
            onOpenSettings: contributionSink.HasPluginSettings(pluginId)
                ? () => contributionSink.OpenPluginSettingsAsync(pluginId)
                : null);

    public void OnSettingsSaved(Action callback) =>
        contributionSink.AddSettingsSavedHandler(pluginId, callback);

    public void AddSessionProvider(SessionProviderRegistration registration) =>
        services.GetRequiredService<IPluginProviderRegistry>().Register(registration);

    public async Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync()
    {
        var profiles = await services.GetRequiredService<ISessionProfileStore>().LoadAsync().ConfigureAwait(false);
        return profiles
            .Select(profile => new PluginProfileInfo(profile.Label, profile.Provider.ToString(), profile.ConfigDir))
            .ToList();
    }

    /// <summary>
    /// Idempotent upsert-by-name into the shared <see cref="IMcpServerStore"/> registry (#60). No entry named
    /// <see cref="McpServerContribution.Name"/> yet → add one (enabled by default, scoped as requested). An
    /// entry already exists → refresh only the plugin-owned <see cref="McpServerConfig.Url"/>/
    /// <see cref="McpServerConfig.Auth"/>/<see cref="McpServerConfig.ApiKey"/>, leaving
    /// <see cref="McpServerConfig.Enabled"/> and <see cref="McpServerConfig.Scope"/> untouched — respects a
    /// server the user disabled or rescoped from the MCP-servers dialog instead of clobbering their choice on
    /// every plugin restart/settings-save. Deliberately does <em>not</em> track "the user deleted this on
    /// purpose": a removed entry is indistinguishable from one never registered, so it comes back as a fresh
    /// (enabled) add the next time the plugin calls this — bounded to the plugin's own trigger points
    /// (<c>Initialize</c>, its settings-saved callback), not a background loop, so it is a re-add on explicit
    /// action rather than a silent fight with the user.
    /// </summary>
    public async Task AddMcpServer(McpServerContribution contribution)
    {
        var store = services.GetRequiredService<IMcpServerStore>();
        var servers = (await store.LoadAsync().ConfigureAwait(false)).ToList();
        var existingIndex = servers.FindIndex(server => string.Equals(server.Name, contribution.Name, StringComparison.Ordinal));

        if (existingIndex < 0)
        {
            servers.Add(new McpServerConfig
            {
                Name = contribution.Name,
                Transport = McpTransport.Http,
                Scope = _ToServerScope(contribution.Scope),
                Url = contribution.Url,
                Auth = _ToAuth(contribution.BearerToken),
                ApiKey = contribution.BearerToken,
            });
        }
        else
        {
            servers[existingIndex] = servers[existingIndex] with
            {
                Transport = McpTransport.Http,
                Url = contribution.Url,
                Auth = _ToAuth(contribution.BearerToken),
                ApiKey = contribution.BearerToken,
            };
        }

        await store.SaveAsync(servers).ConfigureAwait(false);
    }

    // Maps by name, not ordinal — same reasoning as _ToServerScope below.
    private static ToastSeverity _ToToastSeverity(PluginToastSeverity severity) => severity switch
    {
        PluginToastSeverity.Success => ToastSeverity.Success,
        PluginToastSeverity.Warning => ToastSeverity.Warning,
        PluginToastSeverity.Error => ToastSeverity.Error,
        _ => ToastSeverity.Information,
    };

    private static McpServerAuth _ToAuth(string? bearerToken) =>
        string.IsNullOrEmpty(bearerToken) ? McpServerAuth.None : McpServerAuth.ApiKey;

    // Maps by name, not ordinal — Cockpit.Plugins.Abstractions.Mcp.McpContributionScope and Cockpit.Core.Mcp.McpServerScope
    // are declared independently (isolation, see the ICockpitHost doc comment) and are free to diverge in order.
    private static McpServerScope _ToServerScope(McpContributionScope scope) => scope switch
    {
        McpContributionScope.All => McpServerScope.All,
        McpContributionScope.LocalOnly => McpServerScope.LocalOnly,
        McpContributionScope.ClaudeOnly => McpServerScope.ClaudeOnly,
        _ => McpServerScope.All,
    };
}
