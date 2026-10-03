namespace Cockpit.Plugins.Abstractions.Health;

// AC-1466: a plugin's own health, registered through ICockpitHost.AddHealthSection. /healthz reads only the name and
// Healthy; the rows are the details behind it. No free text: the labels are the plugin's, the values status and time.
public interface IPluginHealthSection
{
    /// <summary>
    /// The section's name as /healthz lists it: short, stable and unique to this plugin, like "workflows-scheduler".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The section's state right now. Called on every health request, so it must be cheap and must not block.
    /// </summary>
    PluginHealthReport Read();
}
