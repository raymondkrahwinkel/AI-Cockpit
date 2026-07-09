using Avalonia.Controls;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// What the host offers a plugin during <see cref="ICockpitPlugin.Initialize"/>: the built service
/// provider, the contribution points (an Options tab, a left-menu section), cockpit actions and
/// per-plugin storage. This facade is the contract's only intended growth surface — new capabilities
/// are added here (as default interface methods) rather than by widening the other interfaces.
/// </summary>
public interface ICockpitHost
{
    IServiceProvider Services { get; }

    /// <summary>Adds a tab to the Options dialog; <paramref name="createView"/> builds the tab content (the plugin owns its own view/view-model inside it).</summary>
    void AddOptionsTab(string title, Func<Control> createView);

    /// <summary>Adds an accordion section to the left menu, under the session list.</summary>
    void AddSideMenuSection(string title, Func<Control> createView);

    ICockpitActions Actions { get; }

    IPluginStorage Storage { get; }
}
