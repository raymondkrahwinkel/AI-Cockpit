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
    bool IsBootstrap = false,
    bool HoldsAssistant = false,
    ConnectKeyScope? Scope = null)
{
    public bool IsUsableAt(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is not { } expiresAt || expiresAt > now);

    // AC-1367: a key stored before scopes existed has none, and reads as the default. A method, so cockpit.json
    // never gets a second copy of the scope.
    public ConnectKeyScope EffectiveScope() => Scope ?? ConnectKeyScope.Default;
}

// AC-1367: what one connect key may reach — `NodePairing`'s shape, plus the two grants a pairing always has.
// The default is everything except bypass, so a key issued before this still works and only bypass is taken away.
public sealed record ConnectKeyScope
{
    public IReadOnlyList<string> AllowedProfileLabels { get; init; } = [];
    public IReadOnlyList<string> AllowedProjectIds { get; init; } = [];
    public bool AllowAllProfiles { get; init; } = true;
    public bool AllowAllProjects { get; init; } = true;

    // A profile that skips its approvals runs unsupervised on this machine, so starting one is a grant of its own.
    public bool MayStartBypassProfiles { get; init; }
    public bool MayAnswerPermissions { get; init; } = true;

    public static ConnectKeyScope Default { get; } = new();

    // The bootstrap key's: it is admin and revoked right after setup.
    public static ConnectKeyScope Everything { get; } = new() { MayStartBypassProfiles = true };

    public bool AllowsProfile(string profileLabel) => AllowAllProfiles || AllowedProfileLabels.Contains(profileLabel, StringComparer.Ordinal);

    public bool AllowsProject(string projectId) => AllowAllProjects || AllowedProjectIds.Contains(projectId, StringComparer.Ordinal);
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
