using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The backend's cache of per-session state records, which the frontend primes from the records it already loaded at startup (AC-513).
/// </summary>
public interface ISessionStateCache
{
    /// <summary>
    /// Primes the cache with <paramref name="states"/>; ignored once anything has filled it, since that is newer.
    /// </summary>
    void Seed(IReadOnlyList<SessionStateRecord> states);
}
