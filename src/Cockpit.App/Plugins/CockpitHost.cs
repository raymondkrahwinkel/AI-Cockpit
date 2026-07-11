using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;

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
    IPluginDialogHost dialogHost) : ICockpitHost
{
    public IServiceProvider Services => services;

    public ICockpitActions Actions => actions;

    public IPluginStorage Storage => storage;

    public void AddSettings(Func<Control> createView) =>
        contributionSink.AddPluginSettings(pluginId, createView);

    public void AddSideMenuButton(string title, Action onInvoke) =>
        contributionSink.AddPluginSideButton(title, onInvoke);

    public void AddSideMenuSection(string title, Func<Control> createView) =>
        contributionSink.AddPluginSideSection(title, createView);

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        dialogHost.ShowDialogAsync(title, createContent, width, height);

    public void OnSettingsSaved(Action callback) =>
        contributionSink.AddSettingsSavedHandler(pluginId, callback);
}
