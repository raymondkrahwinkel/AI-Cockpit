namespace Cockpit.Core.Mcp;

// AC-1321: the controller currently holding the line to this node. `SinceUtc` is the first call of this unbroken
// stretch, not the pairing date — it is what the assistant screen shows as "since".
public sealed record ActiveController(string Name, DateTimeOffset SinceUtc);
