namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>One path or process boundary decision for the host's notification center.</summary>
public sealed record PluginGuardViolation(string Mode, string Tool, string Target, bool Denied, string Kind) : PluginSessionEvent;
