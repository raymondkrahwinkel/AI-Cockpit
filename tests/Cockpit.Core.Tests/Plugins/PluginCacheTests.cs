using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="ICockpitHost.Cache"/> (AC-1294, the SDK side of AC-1115): the second cupboard beside
/// <see cref="IPluginStorage"/>, in its own file. What a plugin can rebuild belongs there and not in
/// <c>cockpit.json</c>, where a settings restore replaces a plugin's whole slice — cache included — with the
/// source machine's copy. Asserted through the host's own entry point, not through the file format under it.
/// </summary>
public class PluginCacheTests
{
    /// <summary>
    /// AC-1294 §2: the same <c>Get</c>/<c>Set</c> as <see cref="IPluginStorage"/> — typed, JSON-serialised,
    /// surviving a restart — but landing in the cache's own file, with the settings file never written at all.
    /// </summary>
    [Fact]
    public void WhatAPluginCaches_SurvivesARestart_NeverReachesTheSettingsFile_AndAnUnreadableCacheStartsEmpty()
    {
        var root = _TemporaryRoot();
        try
        {
            var cachePath = Path.Combine(root, PluginCacheStore.FileName);
            var host = _BuildHost(new PluginCacheStore(cachePath).CreateFor("workflows"));

            host.Cache.Set("runs", new[] { 7, 8, 9 });
            host.Cache.Set("last-refresh", "2026-09-08");

            var afterRestart = _BuildHost(new PluginCacheStore(cachePath).CreateFor("workflows"));
            Assert.Equal([7, 8, 9], afterRestart.Cache.Get<int[]>("runs")!);
            Assert.Equal("2026-09-08", afterRestart.Cache.Get<string>("last-refresh"));

            // Another plugin's slice is its own, the way `cockpit.json` keys a plugin's data by its id.
            Assert.Null(_BuildHost(new PluginCacheStore(cachePath).CreateFor("autopilot")).Cache.Get<int[]>("runs"));

            Assert.True(File.Exists(cachePath));
            Assert.False(File.Exists(Path.Combine(root, "cockpit.json")));

            // AC-1294 §4: an unreadable cache file costs a start nothing and the settings nothing — the plugin
            // starts empty and rebuilds, and the settings file beside it is not touched.
            var settingsPath = Path.Combine(root, "cockpit.json");
            const string settings = "{\"Plugins\":{\"workflows\":{\"Data\":{\"workflows\":\"[]\"}}}}";
            File.WriteAllText(settingsPath, settings);
            File.WriteAllText(cachePath, "{ this is not json at all");

            var damaged = _BuildHost(new PluginCacheStore(cachePath).CreateFor("workflows"));

            Assert.Null(damaged.Cache.Get<int[]>("runs"));
            Assert.Equal(settings, File.ReadAllText(settingsPath));

            // And it recovers: the next write replaces the damaged file rather than refusing forever.
            damaged.Cache.Set("runs", new[] { 1 });
            Assert.Equal([1], damaged.Cache.Get<int[]>("runs")!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string _TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cockpit-plugin-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        return root;
    }

    // Typed as the contract a plugin holds, so every assertion goes through `ICockpitHost.Cache` rather than
    // through the store the host happens to have been given.
    private static ICockpitHost _BuildHost(IPluginCache cache) =>
        new DesktopBackendHost(
            "workflows",
            "Workflows",
            Substitute.For<IServiceProvider>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics(),
            cache: cache);

    /// <summary>An older host: implements only what the contract required before the cache existed.</summary>
    public abstract class HostWithoutCache : ICockpitHost
    {
        public IServiceProvider Services => Substitute.For<IServiceProvider>();

        public ICockpitActions Actions => Substitute.For<ICockpitActions>();

        public IPluginStorage Storage => Substitute.For<IPluginStorage>();
    }
}
