using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// The <see cref="ICockpitHost"/> a plugin receives in <see cref="ICockpitPlugin.Initialize"/>: the built
/// service provider, the contribution points routed to the running UI via an
/// <see cref="IPluginContributionSink"/>, the shared cockpit <see cref="ICockpitActions"/>, and this
/// plugin's own <see cref="IPluginStorage"/> slice. Built per plugin (each gets its own storage), so
/// <see cref="Storage"/> is namespaced to the plugin the host was created for.
/// </summary>
internal sealed class CockpitHost(
    IServiceProvider services,
    IPluginContributionSink contributionSink,
    ICockpitActions actions,
    IPluginStorage storage) : ICockpitHost
{
    public IServiceProvider Services => services;

    public ICockpitActions Actions => actions;

    public IPluginStorage Storage => storage;

    public void AddOptionsTab(string title, Func<Control> createView) =>
        contributionSink.AddPluginOptionsTab(title, createView);

    public void AddSideMenuSection(string title, Func<Control> createView) =>
        contributionSink.AddPluginSideSection(title, createView);
}
