namespace Cockpit.Plugins.Abstractions.Health;

// AC-1466: a plugin's own health, registered through ICockpitHost.AddHealthSection. /healthz reads only the name and
// Healthy; the rows are the details behind it, read only by an operate key (AC-1470). Each label is a fixed English
// label naming the thing; never a path, host, user or secret. The values are status and time.
public interface IPluginHealthSection
{
    /// <summary>
    /// The section's name as /healthz lists it, read once at registration: a lowercase slug of at most 40 characters.
    /// Any other name, one already registered, or a section past the host's cap of 16 is ignored.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The section's state right now, read on every health request and given about two seconds.
    /// A read that takes longer or throws counts as unhealthy.
    /// </summary>
    PluginHealthReport Read();
}
