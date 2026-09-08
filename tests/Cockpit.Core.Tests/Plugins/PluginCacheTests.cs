using Cockpit.App.Plugins;
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
    /// AC-1294 §1: <see cref="ICockpitHost.Cache"/> is a default interface member, so a host built against the
    /// SDK from before it existed still loads and still hands the plugin a working cache — proven on a host that
    /// implements only what the contract required back then.
    /// </summary>
    [Fact]
    public void AHostThatPredatesTheCache_StillLoads_AndOffersOneThatWorks()
    {
        ICockpitHost host = Substitute.ForPartsOf<CockpitHostCreateMarkdownViewTests.HostWithoutMarkdownRendering>();

        host.Cache.Set("older-sdk-runs", new[] { "first", "second" });

        Assert.Equal(["first", "second"], host.Cache.Get<string[]>("older-sdk-runs")!);
    }

    /// <summary>
    /// AC-1294 §2: the same <c>Get</c>/<c>Set</c> as <see cref="IPluginStorage"/> — typed, JSON-serialised,
    /// surviving a restart — but landing in the cache's own file, with the settings file never written at all.
    /// </summary>
    [Fact]
    public void WhatAPluginCaches_SurvivesARestart_AndNeverReachesTheSettingsFile()
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
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// AC-1294 §3: no <c>SetSecret</c>/<c>GetSecret</c> on <see cref="IPluginCache"/>, and the absence is the
    /// policy — a cache file has neither the encryption at rest nor the backup scrubbing a declared secret gets.
    /// Pinned here because the obvious way to "finish" the interface is to copy them over from
    /// <see cref="IPluginStorage"/>, which has both.
    /// </summary>
    [Fact]
    public void TheCacheOffersNoWayToStoreACredential()
    {
        var names = typeof(IPluginCache).GetMethods().Select(method => method.Name).ToList();

        Assert.DoesNotContain("SetSecret", names);
        Assert.DoesNotContain("GetSecret", names);
        Assert.Contains("SetSecret", typeof(IPluginStorage).GetMethods().Select(method => method.Name));
    }

    /// <summary>
    /// AC-1294 §4: an unreadable cache file costs a start nothing and costs the settings nothing. The plugin
    /// starts with an empty cache and rebuilds, and the settings file beside it is not touched — it is a
    /// different file, which is the whole point of the split.
    /// </summary>
    [Fact]
    public void AnUnreadableCacheFile_StartsEmpty_AndLeavesTheSettingsAlone()
    {
        var root = _TemporaryRoot();
        try
        {
            var settingsPath = Path.Combine(root, "cockpit.json");
            const string settings = "{\"Plugins\":{\"workflows\":{\"Data\":{\"workflows\":\"[]\"}}}}";
            File.WriteAllText(settingsPath, settings);
            File.WriteAllText(Path.Combine(root, PluginCacheStore.FileName), "{ this is not json at all");

            var host = _BuildHost(new PluginCacheStore(Path.Combine(root, PluginCacheStore.FileName)).CreateFor("workflows"));

            Assert.Null(host.Cache.Get<int[]>("runs"));
            Assert.Equal(settings, File.ReadAllText(settingsPath));

            // And it recovers: the next write replaces the damaged file rather than refusing forever.
            host.Cache.Set("runs", new[] { 1 });
            Assert.Equal([1], host.Cache.Get<int[]>("runs")!);
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
        new CockpitHost(
            "workflows",
            "Workflows",
            Substitute.For<IServiceProvider>(),
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics(),
            cache: cache);
}
