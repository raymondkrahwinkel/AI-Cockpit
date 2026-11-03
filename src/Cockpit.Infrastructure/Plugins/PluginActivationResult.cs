namespace Cockpit.Infrastructure.Plugins;

public sealed record PluginActivationResult(PluginActivationStatus Status, string? Reason = null);
