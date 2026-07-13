using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

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
    IServiceProvider services,
    IPluginContributionSink contributionSink,
    ICockpitActions actions,
    IPluginStorage storage,
    IPluginDialogHost dialogHost,
    ICockpitSessionObserver sessions) : ICockpitHost
{
    public IServiceProvider Services => services;

    public ICockpitActions Actions => actions;

    public IPluginStorage Storage => storage;

    public ICockpitSessionObserver Sessions => sessions;

    public void AddSettings(Func<Control> createView) =>
        contributionSink.AddPluginSettings(pluginId, createView);

    public void AddSideMenuButton(string title, Action onInvoke) =>
        contributionSink.AddPluginSideButton(title, onInvoke);

    public void AddShortcut(PluginShortcut shortcut) =>
        contributionSink.AddPluginShortcut(shortcut);

    public void AddSideMenuSection(string title, Func<Control> createView) =>
        contributionSink.AddPluginSideSection(title, createView);

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        dialogHost.ShowDialogAsync(title, createContent, width, height);

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
