namespace Cockpit.Core.Mcp;

// AC-1321: the controller currently holding the line; `SinceUtc` is this unbroken stretch's first call, the screen's
// "since". AC-1405: `KeyPrefix`, `KeyScope` and `ExpiresAt` are the holding connect key's, null for the pairing; a
// holder whose key expired or was revoked is no holder, for routing and for reading alike.
public sealed record ActiveController(string Name, DateTimeOffset SinceUtc, string? KeyPrefix = null, ConnectKeyScope? KeyScope = null, DateTimeOffset? ExpiresAt = null)
{
    // What the screen says while this controller holds the line. Local clock, short — it is read by someone sitting at
    // this machine. One text for the assistant's host and its chat window (AC-1440), so a scene says the same thing.
    public string TakeoverReason() =>
        $"Controlled by {Name} since {SinceUtc.ToLocalTime():HH:mm}. "
        + "Your assistant here comes back by itself when that connection drops.";
}
