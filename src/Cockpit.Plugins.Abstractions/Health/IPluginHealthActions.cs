namespace Cockpit.Plugins.Abstractions.Health;

// AC-1470: the actions a health section offers on its rows ("Run now"), implemented next to IPluginHealthSection by a
// section that has any. A section without it offers none; a remote operate key may run what it offers.
public interface IPluginHealthActions
{
    /// <summary>
    /// Runs the action a row of this section named as its ActionId, once the host has found that row in the caller's scope.
    /// </summary>
    Task<PluginHealthActionResult> RunAsync(string actionId, CancellationToken cancellationToken);
}
