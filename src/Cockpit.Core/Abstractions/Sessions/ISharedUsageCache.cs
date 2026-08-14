using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

// The dedupe behind AC-775: N sessions running under the same underlying credential (not the same profile
// label — two profiles can share one API key/ConfigDir) used to each poll their provider for usage
// independently. The first session to see a fresh reading writes it here; every other session under that
// credential reads it back within the TTL instead of triggering its own fetch. Keyed on the credential a
// `ProviderConfig` identifies, never on `SessionProfile.Label`.
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
