using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Sessions;

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

    /// <summary>
    /// Registers <paramref name="callback"/> to run (on the UI thread) after this plugin's own settings are
    /// saved from the plugin manager's gear (#52) — i.e. <see cref="IPluginSettingsView.Save"/> returned
    /// true. A contribution that read settings once at construction and cached the result (e.g. a side-menu
    /// section's already-fetched list) should subscribe here and reload, so a settings change takes effect
    /// immediately instead of requiring an app restart. A contribution that reads <see cref="IPluginStorage"/>-backed
    /// settings fresh on every access (the common case — see <see cref="Storage"/>) already reflects a save
    /// without this. Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older
    /// plugin builds) keep compiling untouched — only the app's own host overrides it.
    /// </summary>
    void OnSettingsSaved(Action callback)
    {
    }

    /// <summary>
    /// Registers a new session provider (#45) — the plugin equivalent of the built-in Claude-CLI/Ollama/LM-Studio
    /// providers: it becomes selectable in the New-session/Manage-profiles provider picker, backed by the
    /// plugin's own <see cref="IPluginSessionDriver"/> and config view. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched —
    /// only the app's own host overrides it.
    /// </summary>
    void AddSessionProvider(SessionProviderRegistration registration)
    {
    }
}
