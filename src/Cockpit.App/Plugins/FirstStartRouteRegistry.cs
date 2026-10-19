using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.UI.Onboarding;

namespace Cockpit.App.Plugins;

/// <summary>
/// The first-start routes plugins register (<c>ICockpitUiHost.AddFirstStartRoute</c>) for the wizard's "How do you want
/// to start?" step. Same shape as <see cref="IWorkspaceTypeRegistry"/>: a list plus a change event, because plugins
/// initialize after the cockpit's view models are built.
/// </summary>
public interface IFirstStartRouteRegistry
{
    /// <summary>
    /// Records a route and the name of the plugin that offers it. A route id that is already registered is refused.
    /// </summary>
    /// <returns>
    /// False when another registration already holds this route id — the caller says so; nothing throws.
    /// </returns>
    bool Register(FirstStartRoute route, string pluginName);

    /// <summary>
    /// Raised when a route is registered, so a step that is already on screen can list it.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Every registered route in registration order, each with the name of the plugin offering it.
    /// </summary>
    IReadOnlyList<(FirstStartRoute Route, string PluginName)> Routes { get; }
}

internal sealed class FirstStartRouteRegistry : IFirstStartRouteRegistry, ISingletonService
{
    private readonly Lock _gate = new();
    private readonly List<(FirstStartRoute Route, string PluginName)> _routes = [];

    public event EventHandler? Changed;

    public IReadOnlyList<(FirstStartRoute Route, string PluginName)> Routes
    {
        get
        {
            lock (_gate)
            {
                return [.. _routes];
            }
        }
    }

    public bool Register(FirstStartRoute route, string pluginName)
    {
        lock (_gate)
        {
            if (_routes.Any(entry => entry.Route.Id == route.Id))
            {
                return false;
            }

            _routes.Add((route, pluginName));
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }
}
