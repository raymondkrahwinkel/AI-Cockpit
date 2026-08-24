using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

// AC-775: dedupes usage polling across N sessions sharing one credential (not profile label — profiles can
// share a key/ConfigDir). First session to get a fresh reading writes it; others reuse it within the TTL.
public interface ISharedUsageCache
{
    /// <summary>The freshest status recorded for <paramref name="config"/>'s credential, or null when there is
    /// none within the TTL — including when <paramref name="config"/> identifies no cacheable credential (a
    /// local model, or no profile at all).</summary>
    SessionStatusFeed? TryGet(ProviderConfig? config);

    /// <summary>Records a session's own fresh reading for <paramref name="config"/>'s credential. A no-op when
    /// <paramref name="config"/> identifies no cacheable credential.</summary>
    void Set(ProviderConfig? config, SessionStatusFeed status);
}
