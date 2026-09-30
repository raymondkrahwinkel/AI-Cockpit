using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// The cache behind AC-775, independent of the SessionViewModel wiring: keyed on the underlying credential a
/// <see cref="ProviderConfig"/> identifies, never on a profile's label, with a plain TTL and no separate
/// invalidation path.
/// </summary>
public class SharedUsageCacheTests
{
    private static readonly SessionStatusFeed Status = new(42, [new SessionRateWindow("5h", 60, null, null)]);

    /// <summary>A clock that only moves when a test moves it, so the TTL edge is exact rather than a race.</summary>
    private sealed class StoppedClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void TwoConfigs_DifferentCredential_NeverShareAnEntry()
    {
        var cache = new SharedUsageCache();
        cache.Set(new ClaudeConfig(@"C:\fake\.claude-a"), Status);

        Assert.Null(cache.TryGet(new ClaudeConfig(@"C:\fake\.claude-b")));
    }

    [Fact]
    public void PluginConfigs_DifferentConfigJson_NeverShareAnEntry()
    {
        var cache = new SharedUsageCache();
        cache.Set(new PluginProviderConfig("codex", "{\"apiKey\":\"k1\"}"), Status);

        Assert.Null(cache.TryGet(new PluginProviderConfig("codex", "{\"apiKey\":\"k2\"}")));
    }

    public static IEnumerable<object?[]> _UncacheableConfigs()
    {
        yield return [new LmStudioConfig("http://localhost:1234", "some-model")];
        yield return [null];
    }

}
