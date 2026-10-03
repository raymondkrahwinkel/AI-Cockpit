namespace Cockpit.Plugins.Abstractions.Health;

// AC-1470: how an action went. A value, not text, so the answer cannot carry anything the plugin read.
public sealed record PluginHealthActionResult(bool Succeeded);
