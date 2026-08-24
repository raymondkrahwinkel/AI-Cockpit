using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// The concrete cache behind ISharedUsageCache (AC-775). A plain dictionary + timestamp rather than
// IMemoryCache: that dependency exists nowhere else in this codebase, and would be overkill for what is one
// TTL check per read.
internal sealed class SharedUsageCache : ISharedUsageCache, ISingletonService
{
    // Matches the Claude SDK route's own `_AllowancePollInterval` — reading a figure this old is no staler
    // than what the header already shows today, and short enough that a real rate-limit window (5h/weekly)
    // never visibly drifts.
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (SessionStatusFeed Status, DateTimeOffset RecordedAt)> _entries = new();
    private readonly TimeProvider _time;

    public SharedUsageCache() : this(TimeProvider.System)
    {
    }

    // For tests, which need to move time past the TTL rather than wait a minute out.
    internal SharedUsageCache(TimeProvider time) => _time = time;

    public SessionStatusFeed? TryGet(ProviderConfig? config)
    {
        if (_KeyFor(config) is not { } key
            || !_entries.TryGetValue(key, out var entry)
            || _time.GetUtcNow() - entry.RecordedAt > Ttl)
        {
            return null;
        }

        return entry.Status;
    }

    public void Set(ProviderConfig? config, SessionStatusFeed status)
    {
        if (_KeyFor(config) is { } key)
        {
            _entries[key] = (status, _time.GetUtcNow());
        }
    }

    // The underlying credential a reading belongs to, not the profile's label — so two profiles sharing one
    // account share one entry (the AC-775 regression). Ollama/LmStudio and a profile-less session report nothing
    // cacheable. A plugin's whole ConfigJson stands in for its opaque credential fields (#45 fase B1).
    private static string? _KeyFor(ProviderConfig? config) => config switch
    {
        ClaudeConfig claude => $"claude:{claude.ConfigDir}",
        PluginProviderConfig plugin => $"plugin:{plugin.ProviderId}:{plugin.ConfigJson}",
        _ => null,
    };
}
