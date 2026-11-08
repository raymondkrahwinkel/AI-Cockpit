namespace Cockpit.Core.Plugins;

// AC-1980: a plugin stays with the store it came from and never moves on its own.
public static class PluginSourceBinding
{
    // The recorded source, else the only store offering the plugin (AC-1980 criterion 5), else null — then the old rule (first store wins) still applies.
    public static string? Resolve(string? recorded, IEnumerable<string> offeringKeys)
    {
        if (!string.IsNullOrEmpty(recorded))
        {
            return recorded;
        }

        var distinct = offeringKeys.Distinct(StringComparer.Ordinal).Take(2).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }
}
