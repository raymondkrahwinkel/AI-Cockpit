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
    public async Task AReadableRevocationFeed_WarnsOnEveryPassEvenWhenTheInstalledPluginWasRemovedFromTheFeed()
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
    }
}
