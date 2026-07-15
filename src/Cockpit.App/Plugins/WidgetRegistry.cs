using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the widget types plugins register (<c>ICockpitHost.AddWidget</c>), so a Dashboard workspace's
/// "Add widget" gallery can offer them and a saved dashboard can rebuild its instances. A registry of its own
/// — the same shape as <see cref="ConversationPickerRegistry"/> and the workflow registries — rather than a
/// collection on a view model, so the gallery reads it without the two depending on each other. Empty is the
/// normal case until a widget-providing plugin is installed.
/// </summary>
public interface IWidgetRegistry
{
    /// <summary>
    /// Records a widget type along with what its owning plugin brought: storage, the observe surface, and the
    /// keys it declared as credentials. A type id that is already registered is refused, first one wins.
    /// </summary>
    /// <returns>False when another plugin already contributes this type id — the caller says so; nothing throws.</returns>
    bool Register(WidgetRegistration widget, IPluginStorage pluginStorage, ICockpitSessionObserver sessions, IReadOnlyList<string> declaredSecretKeys);

    /// <summary>Every credential key any widget-providing plugin declared — what an export scrubs beyond the name rule.</summary>
    IReadOnlyList<string> DeclaredSecretKeys { get; }

    /// <summary>
    /// Raised when a plugin contributes a widget. Plugins initialize after the cockpit's view models are built,
    /// so anything reading <see cref="Widgets"/> would otherwise read an empty list once, at startup, and never
    /// hear about the widgets that arrived a moment later — which is exactly how the "Add widget" button stayed
    /// disabled with two widgets installed.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>Every widget type registered so far, in registration order — what the gallery lists.</summary>
    IReadOnlyList<WidgetRegistration> Widgets { get; }

    /// <summary>
    /// Builds the context for one placed instance of <paramref name="widgetId"/>, or null when no plugin
    /// contributes that type — an uninstalled or disabled plugin leaves its widgets behind in a saved
    /// dashboard, and that pane has to be skippable rather than fatal.
    /// </summary>
    (WidgetRegistration Registration, WidgetContext Context)? CreateInstance(string widgetId, string instanceId);

    /// <summary>Whether a plugin here contributes <paramref name="widgetId"/> — what an import asks before placing a pane it may not be able to render.</summary>
    bool IsInstalled(string widgetId);
}

internal sealed class WidgetRegistry : IWidgetRegistry, ISingletonService
{
    private readonly List<RegisteredWidget> _widgets = [];

    public event EventHandler? Changed;

    public IReadOnlyList<WidgetRegistration> Widgets => [.. _widgets.Select(widget => widget.Registration)];

    /// <remarks>
    /// The union across every widget-providing plugin, not per widget. Over-scrubbing costs a plugin a setting
    /// whose name another plugin declared secret; under-scrubbing ships a live credential in a file you meant
    /// to share. Of the two, the first is the one you can afford — and a plain setting named "pat" or
    /// "credential" is not a thing anyone writes by accident.
    /// </remarks>
    public IReadOnlyList<string> DeclaredSecretKeys =>
        [.. _widgets.SelectMany(widget => widget.DeclaredSecretKeys).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// First registration of a type id wins, and a later one is refused rather than added beside it. Two
    /// plugins can claim the same id — nothing stops a third party picking one that exists, and the cockpit's
    /// own clock did exactly that when it was split out of the reference-widgets plugin. Adding both put the
    /// type in the gallery twice and left <see cref="CreateInstance"/> silently resolving to whichever plugin
    /// happened to load first, which is not a thing an operator can see, let alone fix.
    /// </summary>
    public bool Register(WidgetRegistration widget, IPluginStorage pluginStorage, ICockpitSessionObserver sessions, IReadOnlyList<string> declaredSecretKeys)
    {
        if (IsInstalled(widget.Id))
        {
            return false;
        }

        _widgets.Add(new RegisteredWidget(widget, pluginStorage, sessions, declaredSecretKeys));
        Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }

    public bool IsInstalled(string widgetId) => _widgets.Any(widget => widget.Registration.Id == widgetId);

    public (WidgetRegistration Registration, WidgetContext Context)? CreateInstance(string widgetId, string instanceId)
    {
        if (_widgets.FirstOrDefault(widget => widget.Registration.Id == widgetId) is not { } registered)
        {
            return null;
        }

        return (registered.Registration, new WidgetContext(instanceId, registered.PluginStorage, registered.Sessions));
    }
}
