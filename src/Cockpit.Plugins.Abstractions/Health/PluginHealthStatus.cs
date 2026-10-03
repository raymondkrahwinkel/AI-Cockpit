namespace Cockpit.Plugins.Abstractions.Health;

// AC-1466: a row's state. A value, not text, so a row cannot carry anything the plugin read.
public enum PluginHealthStatus
{
    Ok,
    Failed,
}
