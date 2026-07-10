using Avalonia.Controls;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// What the host offers a plugin during <see cref="ICockpitPlugin.Initialize"/>: the built service
/// provider, cockpit actions, per-plugin storage, and the contribution points — a settings view (opened
/// from the plugin manager's gear), a left-menu launcher button and/or an inline left-menu section, and a
/// helper to open a modal dialog. This facade is the contract's only intended growth surface — new
/// capabilities are added here (as default interface methods) rather than by widening the other interfaces.
/// </summary>
public interface ICockpitHost
{
    IServiceProvider Services { get; }

    ICockpitActions Actions { get; }

    IPluginStorage Storage { get; }

    /// <summary>Registers the plugin's settings view, opened from the gear next to the plugin in the plugin manager. Call at most once.</summary>
    void AddSettings(Func<Control> createView);

    /// <summary>Adds a launcher button to the left menu; clicking runs <paramref name="onInvoke"/> — typically opening a dialog via <see cref="ShowDialogAsync"/>.</summary>
    void AddSideMenuButton(string title, Action onInvoke);

    /// <summary>Adds an inline accordion section to the left menu, under the session list — for small, always-visible content.</summary>
    void AddSideMenuSection(string title, Func<Control> createView);

    /// <summary>Opens a modal dialog over the main window hosting <paramref name="createContent"/>; the plugin owns the content control.</summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);
}
