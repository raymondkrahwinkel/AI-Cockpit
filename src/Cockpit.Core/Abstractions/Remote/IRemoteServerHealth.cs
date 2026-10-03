namespace Cockpit.Core.Abstractions.Remote;

/// <summary>
/// What a connect server says about its own health through <c>GET /api/v1/health</c> (AC-1457), as far as this key's
/// scope reaches. Nothing is filtered here: the server decides what a key sees.
/// </summary>
public interface IRemoteServerHealth
{
    /// <summary>
    /// The last answer; null until the server first answered, or when this key may not read the health.
    /// </summary>
    RemoteServerHealth? Current { get; }

    /// <summary>
    /// Whether a tab shows the health now; the server is then asked more often.
    /// </summary>
    bool IsWatched { get; set; }

    /// <summary>
    /// Raised on any thread when <see cref="Current"/> changed.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Asks the server again and raises <see cref="Changed"/> once it answered.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the action a health row offers on the server, and asks again; false when the server says it did not run.
    /// </summary>
    Task<bool> RunActionAsync(string section, string actionId, CancellationToken cancellationToken = default);
}

// AC-1457: the sign-in states the server words as "signedIn", "expired" and "unchecked".
public sealed record RemoteProfileHealth(
    string Label,
    string Provider,
    string SignIn,
    DateTimeOffset? LastCheck,
    DateTimeOffset? ExpiredSince,
    DateTimeOffset? AnnouncedAt);

public sealed record RemoteKeyHealth(string Label, string Capability);

public sealed record RemoteServerFacts(
    string? Version,
    string? Image,
    DateTimeOffset? StartedAt,
    string? Address,
    string? AssistantHolder,
    IReadOnlyList<RemoteKeyHealth> Keys);

public sealed record RemoteHealthRow(string Label, bool Failed, DateTimeOffset? At, string? ActionId);

public sealed record RemoteHealthSection(string Name, bool Healthy, IReadOnlyList<RemoteHealthRow> Rows);

public sealed record RemoteServerHealth(
    IReadOnlyList<RemoteProfileHealth> Profiles,
    RemoteServerFacts Server,
    IReadOnlyList<RemoteHealthSection> Sections);
