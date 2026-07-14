using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the widget types plugins register (<c>ICockpitHost.AddWidget</c>), so a Dashboard workspace's
/// "Add widget" gallery can offer them. A registry of its own — the same shape as
/// <see cref="ConversationPickerRegistry"/> and the workflow registries — rather than a collection on a view
/// model, so the gallery reads it without the two depending on each other. Empty is the normal case until a
/// widget-providing plugin is installed.
/// </summary>
public interface IWidgetRegistry
{
    void Register(WidgetRegistration widget);

    /// <summary>Every widget type registered so far, in registration order.</summary>
    IReadOnlyList<WidgetRegistration> Widgets { get; }
}

internal sealed class WidgetRegistry : IWidgetRegistry, ISingletonService
{
    private readonly List<WidgetRegistration> _widgets = [];

    public IReadOnlyList<WidgetRegistration> Widgets => _widgets;

    public void Register(WidgetRegistration widget) => _widgets.Add(widget);
}
