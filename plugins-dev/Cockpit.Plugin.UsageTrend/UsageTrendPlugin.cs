using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.UsageTrend.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.UsageTrend;

// The usage-trend widget's plugin (AC-54). AC-1395 split: the widget lives in the UI part now (UsageTrendUi),
// which reaches the cache-backed history behind ICockpitHost.Cache over the plugin's own channel — the debounce,
// jump and retention rules (UsageTrendHistory) stay here, next to the cache they gate writes to.
public sealed class UsageTrendPlugin : ICockpitPlugin
{
    private readonly List<IDisposable> _handlers = [];

    public PluginMetadata Metadata { get; } = new(
        Id: "usage-trend",
        DisplayName: "Usage Trend",
        Author: "Cockpit",
        Description: "Charts the context / 5h / weekly usage of your sessions over time, per profile, on a Dashboard workspace.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _handlers.Add(host.Channel.Handle(UsageTrendChannel.Get, (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<UsageTrendHistoryRequest>(UsageTrendChannel.Json)
                ?? throw new ArgumentException("The request names no widget instance.", nameof(payload));
            return Task.FromResult(JsonSerializer.SerializeToElement(_Load(host, request.InstanceId), UsageTrendChannel.Json));
        }));

        _handlers.Add(host.Channel.Handle(UsageTrendChannel.Append, (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<UsageTrendAppendRequest>(UsageTrendChannel.Json)
                ?? throw new ArgumentException("The request names no widget instance.", nameof(payload));
            var existing = _Load(host, request.InstanceId);
            var updated = UsageTrendHistory.Append(existing, request.Candidate) ?? existing;
            host.Cache.Set(_CacheKey(request.InstanceId), updated);
            return Task.FromResult(JsonSerializer.SerializeToElement(updated, UsageTrendChannel.Json));
        }));

        _handlers.Add(host.Channel.Handle(UsageTrendChannel.Seed, (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<UsageTrendSeedRequest>(UsageTrendChannel.Json)
                ?? throw new ArgumentException("The request names no widget instance.", nameof(payload));
            if (host.Cache.Get<List<UsageTrendSample>>(_CacheKey(request.InstanceId)) is null)
            {
                host.Cache.Set(_CacheKey(request.InstanceId), request.History);
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(true, UsageTrendChannel.Json));
        }));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }

    // Matches the key the widget cached history under before AC-1395, so an existing install's history survives.
    private static string _CacheKey(string instanceId) => $"widget:{instanceId}:history";

    private static IReadOnlyList<UsageTrendSample> _Load(ICockpitHost host, string instanceId)
    {
        var stored = host.Cache.Get<List<UsageTrendSample>>(_CacheKey(instanceId)) ?? [];
        return UsageTrendHistory.Prune(stored, DateTimeOffset.UtcNow);
    }
}
