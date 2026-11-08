using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Plugins;
using Cockpit.Core.Toasts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

public sealed class PluginPublisherWarningTests
{
    [Fact]
    public async Task AReadableRevocationFeed_WarnsOnEveryPassEvenWhenTheInstalledPluginWasRemovedFromTheFeed_AndAHigherVersionFromAnotherSourceIsNoUpdate()
    {
        Assert.True(PluginManifest.TryParse("""{"id":"acme","name":"Acme","version":"1.0.0","entryAssembly":"Plugin.dll","abstractionsVersion":3}""", out var manifest, out _));
        Assert.NotNull(manifest);
        var plugin = new DiscoveredPlugin("unused", "acme", manifest, "pin", PluginLoadDecision.Load);
        var store = PluginStoreConfig.Remote("https://depot.example/cockpit/stores/acme/index.json") with
        {
            Signed = new PluginStoreSignedSource("depot:depot.example/acme", "acme", () => new HttpClientHandler()),
        };
        var sources = Substitute.For<IPluginStoreSources>();
        sources.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { new PluginStoreSourceInfo(store) });
        var client = Substitute.For<IPluginStoreClient>();
        client.FetchIndexAsync(store, Arg.Any<CancellationToken>()).Returns(new PluginStoreFetchResult(true, null,
            new PluginStoreIndex("Acme", [], RevokedKeys: [new("remembered-key", null, null)]), store.Location));
        var trust = Substitute.For<IPluginTrustStore>();
        trust.GetAsync("acme", Arg.Any<CancellationToken>()).Returns(new PluginTrustEntry("depot:depot.example/acme", "remembered-key"));
        var toast = Substitute.For<IToastService>();
        var checker = new PluginUpdateChecker(_ => Task.FromResult<IReadOnlyList<DiscoveredPlugin>>([plugin]),
            Substitute.For<IPluginStoreConfigStore>(), client, toast, new CockpitViewModel(), NullLogger<PluginUpdateChecker>.Instance,
            storeSources: sources, setUpdateBadgeCount: _ => { }, trust: trust);

        await checker.CheckNowAsync();
        await checker.CheckNowAsync();

        toast.Received(2).Show(Arg.Is<string>(message => message.Contains("revoked") && message.Contains("remembered-key")), ToastSeverity.Warning);
        client.FetchIndexAsync(store, Arg.Any<CancellationToken>()).Returns(new PluginStoreFetchResult(true, null, new PluginStoreIndex("Acme", []), store.Location));
        await checker.CheckNowAsync();
        toast.Received(2).Show(Arg.Any<string>(), ToastSeverity.Warning);

        // AC-1980: bound to the Depot store, a higher version in the public store is no update — recorded source, or the only store that offered it before.
        static PluginStoreIndex Offering(string version) => new("S", [new("acme", "Acme", null, null, version, [new(version, "plugin.zip", null, null, null, null)])]);
        var publicStore = PluginStoreConfig.Remote("https://public.example/index.json");
        client.FetchIndexAsync(store, Arg.Any<CancellationToken>()).Returns(new PluginStoreFetchResult(true, null, Offering("1.0.0"), store.Location));
        client.FetchIndexAsync(publicStore, Arg.Any<CancellationToken>()).Returns(new PluginStoreFetchResult(true, null, Offering("9.0.0"), publicStore.Location));
        sources.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { new PluginStoreSourceInfo(publicStore), new PluginStoreSourceInfo(store) });
        var registrations = Substitute.For<IPluginRegistrationStore>();
        var badges = new List<int>();
        var bound = new PluginUpdateChecker(_ => Task.FromResult<IReadOnlyList<DiscoveredPlugin>>([plugin]),
            Substitute.For<IPluginStoreConfigStore>(), client, toast, new CockpitViewModel(), NullLogger<PluginUpdateChecker>.Instance,
            storeSources: sources, setUpdateBadgeCount: badges.Add, registrations: registrations);
        registrations.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, PluginRegistration> { ["acme"] = new(true, "pin", Source: store.SourceKey) });
        await bound.CheckNowAsync();
        registrations.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, PluginRegistration> { ["acme"] = new(true, "pin") });
        await bound.CheckNowAsync();
        registrations.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, PluginRegistration> { ["acme"] = new(true, "pin", Source: publicStore.SourceKey) });
        await bound.CheckNowAsync();

        Assert.Equal([0, 1, 1], badges);
    }
}
