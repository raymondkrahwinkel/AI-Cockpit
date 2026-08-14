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
    public void TwoConfigs_SameCredential_DifferentLabel_ShareOneEntry()
    {
        var cache = new SharedUsageCache();
        var profileA = new ClaudeConfig(@"C:\fake\.claude");
        var profileB = new ClaudeConfig(@"C:\fake\.claude");

        cache.Set(profileA, Status);

        Assert.Equal(Status, cache.TryGet(profileB));
    }

    [Fact]
    public void TwoConfigs_DifferentCredential_NeverShareAnEntry()
    {
        var cache = new SharedUsageCache();
        cache.Set(new ClaudeConfig(@"C:\fake\.claude-a"), Status);

        Assert.Null(cache.TryGet(new ClaudeConfig(@"C:\fake\.claude-b")));
    }

    [Fact]
    public void PluginConfigs_SameProviderIdAndConfigJson_ShareOneEntry()
    {
        var cache = new SharedUsageCache();
        cache.Set(new PluginProviderConfig("codex", "{\"apiKey\":\"k1\"}"), Status);

        Assert.Equal(Status, cache.TryGet(new PluginProviderConfig("codex", "{\"apiKey\":\"k1\"}")));
    }

    [Fact]
    public void PluginConfigs_DifferentConfigJson_NeverShareAnEntry()
    {
        var cache = new SharedUsageCache();
        cache.Set(new PluginProviderConfig("codex", "{\"apiKey\":\"k1\"}"), Status);

        Assert.Null(cache.TryGet(new PluginProviderConfig("codex", "{\"apiKey\":\"k2\"}")));
    }

    [Theory]
    [MemberData(nameof(_UncacheableConfigs))]
    public void UncacheableConfig_NeverTouchesTheCache(ProviderConfig? config)
    {
        var cache = new SharedUsageCache();

        cache.Set(config, Status);

        Assert.Null(cache.TryGet(config));
    }

    public static IEnumerable<object?[]> _UncacheableConfigs()
    {
        yield return [new LmStudioConfig("http://localhost:1234", "some-model")];
        yield return [null];
    }

    [Fact]
    public void EmptyCache_FallsBackToNull_ExactlyLikeNoSharedCacheAtAll()
    {
        var cache = new SharedUsageCache();

        Assert.Null(cache.TryGet(new ClaudeConfig(@"C:\fake\.claude")));
    }

    [Fact]
    public void PastTheTtl_TheEntryStopsAnswering()
    {
        var clock = new StoppedClock(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = new SharedUsageCache(clock);
        var config = new ClaudeConfig(@"C:\fake\.claude");
        cache.Set(config, Status);

        clock.Advance(SharedUsageCache.Ttl - TimeSpan.FromSeconds(1));
        Assert.Equal(Status, cache.TryGet(config));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(cache.TryGet(config));
    }
}
