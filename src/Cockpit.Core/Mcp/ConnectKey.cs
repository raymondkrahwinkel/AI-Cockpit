namespace Cockpit.Core.Mcp;

// AC-1351 (B3): what a connect key may do on the node endpoint. Operate is the node tools; Admin adds managing
// the keys themselves. Two capabilities rather than a role model — the pairing secret counts as Operate.
public enum ConnectKeyCapability
{
    Operate,
    Admin,
}

// AC-1351: a key that opens the node endpoint without a pairing, after DEP-208 — only its SHA-256 and a short
// prefix are stored, never the key itself. `ExpiresAt` is null only for the bootstrap key, which lives exactly as
// long as the secret that supplies it; a revoked bootstrap key is persisted so the same secret cannot revive it.
public sealed record ConnectKey(
    string Prefix,
    string Hash,
    ConnectKeyCapability Capability,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt = null,
    bool IsBootstrap = false)
{
    public bool IsUsableAt(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is not { } expiresAt || expiresAt > now);
}

// AC-1351: the door's policy values, read from cockpit.json at startup. The defaults are the grooming's: ten
// failures within ten minutes lock an address out for a minute, doubling per repeat up to an hour.
public sealed record ConnectKeyPolicy
{
    public int FailuresBeforeLockout { get; init; } = 10;
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan FirstLockout { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaxLockout { get; init; } = TimeSpan.FromMinutes(60);
    public int DefaultExpiryDays { get; init; } = 30;
    public int MaxExpiryDays { get; init; } = 365;

    // How many addresses (an IPv6 /64 counts as one) the lockout tracks at once; past it the least recent is evicted.
    public int MaxTrackedAddresses { get; init; } = 4096;

    public static ConnectKeyPolicy Default { get; } = new();
}
