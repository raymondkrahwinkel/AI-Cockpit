using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// AC-1446: manages a server's connect keys, lockouts and access audit, as an admin key calling it.
/// The caller is the one the request came in with, never an argument, so the node tools and the API share one audit.
/// </summary>
public interface IConnectKeyAdministration
{
    /// <summary>
    /// The keys without their secrets or hashes, the addresses locked out now, and the lockout policy.
    /// </summary>
    Task<ConnectKeyOverview> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a key; the returned secret is the only copy there will ever be.
    /// </summary>
    Task<IssuedConnectKey> IssueAsync(ConnectKeyRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// False when no live key has <paramref name="prefix"/>; the bootstrap key is refused with an <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<bool> RevokeAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// False when no live key has <paramref name="prefix"/>; the bootstrap key is refused with an <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<bool> SetScopeAsync(string prefix, ConnectKeyScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// False when <paramref name="address"/> is not locked out.
    /// </summary>
    Task<bool> LiftLockoutAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to <paramref name="count"/> audit entries written before the one with id <paramref name="before"/> (the newest when null), newest first.
    /// </summary>
    Task<IReadOnlyList<ConnectKeyAuditEntry>> ReadAuditAsync(long? before, int count, CancellationToken cancellationToken = default);
}

public sealed record ConnectKeyOverview(IReadOnlyList<ConnectKeyInfo> Keys, IReadOnlyList<ConnectKeyLockout> Lockouts, ConnectKeyPolicy Policy);

// AC-1446: a key as an admin sees it — never its secret or hash. `LastUsedFrom` is the address of that last use.
public sealed record ConnectKeyInfo(
    string Prefix,
    string Label,
    ConnectKeyCapability Capability,
    bool IsBootstrap,
    bool HoldsAssistant,
    ConnectKeyScope Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt,
    string? LastUsedFrom);

public sealed record ConnectKeyLockout(string Address, DateTimeOffset LockedUntil, int RefusedWhileLockedOut);

public sealed record ConnectKeyRequest(string Label, ConnectKeyCapability Capability, int? ExpiresInDays, bool HoldsAssistant, ConnectKeyScope Scope);

public sealed record IssuedConnectKey(ConnectKeyInfo Key, string Secret);

// AC-1446: one line of the node access audit. `Id` is unique and rises with the write order, the paging cursor; `Actor`
// is the calling key's label, `KeyPrefix` its prefix, and `Subject` what was acted on: a key's prefix or a lifted address.
public sealed record ConnectKeyAuditEntry(long Id, DateTimeOffset At, string Address, string? Actor, string? KeyPrefix, string? Action, string Outcome, string? Subject);
