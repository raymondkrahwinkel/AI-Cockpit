namespace Cockpit.Core.Mcp;

// AC-1321: the controller currently holding the line to this node. `SinceUtc` is the first call of this unbroken
// stretch, not the pairing date — it is what the assistant screen shows as "since".
// AC-1405: `KeyPrefix` and `KeyScope` are the holding connect key's, both null for the pairing — the mail routed
// to this controller answers to that scope, as every other node tool does.
public sealed record ActiveController(string Name, DateTimeOffset SinceUtc, string? KeyPrefix = null, ConnectKeyScope? KeyScope = null)
{
    // What the screen says while this controller holds the line. Local clock, short — it is read by someone sitting at
    // this machine. One text for the assistant's host and its chat window (AC-1440), so a scene says the same thing.
    public string TakeoverReason() =>
        $"Controlled by {Name} since {SinceUtc.ToLocalTime():HH:mm}. "
        + "Your assistant here comes back by itself when that connection drops.";
}
