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
    /// <summary>Records a widget type along with its plugin's storage and observe surface, which a placed instance needs later.</summary>
    void Register(WidgetRegistration widget, IPluginStorage pluginStorage, ICockpitSessionObserver sessions);

    /// <summary>Every widget type registered so far, in registration order — what the gallery lists.</summary>
    IReadOnlyList<WidgetRegistration> Widgets { get; }

    /// <summary>
    /// Builds the context for one placed instance of <paramref name="widgetId"/>, or null when no plugin
    /// contributes that type — an uninstalled or disabled plugin leaves its widgets behind in a saved
    /// dashboard, and that pane has to be skippable rather than fatal.
    /// </summary>
    (WidgetRegistration Registration, WidgetContext Context)? CreateInstance(string widgetId, string instanceId);
}

internal sealed class WidgetRegistry : IWidgetRegistry, ISingletonService
{
    private readonly List<RegisteredWidget> _widgets = [];

    public IReadOnlyList<WidgetRegistration> Widgets => [.. _widgets.Select(widget => widget.Registration)];

    public void Register(WidgetRegistration widget, IPluginStorage pluginStorage, ICockpitSessionObserver sessions) =>
        _widgets.Add(new RegisteredWidget(widget, pluginStorage, sessions));

    public (WidgetRegistration Registration, WidgetContext Context)? CreateInstance(string widgetId, string instanceId)
    {
        if (_widgets.FirstOrDefault(widget => widget.Registration.Id == widgetId) is not { } registered)
        {
            return null;
        }

        return (registered.Registration, new WidgetContext(instanceId, registered.PluginStorage, registered.Sessions));
    }
}
