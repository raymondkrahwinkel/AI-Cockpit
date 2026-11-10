namespace Cockpit.Plugins.Abstractions.Channels;

public sealed record OperatorSessionResult(bool Ok, string? Error, string? PaneId = null);
