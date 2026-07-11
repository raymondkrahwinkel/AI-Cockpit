using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Plugins;
using Cockpit.Core.Tests.Voice;
using Cockpit.Core.Toasts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// <see cref="PluginUpdateChecker"/> (#59): detects a newer store version for an installed plugin, stays
/// quiet when there is nothing new or a store is unreachable, and never re-toasts the same (plugin,
/// version) update on a later pass — the behaviour the 15-minute timer in <c>App</c> relies on. The
/// installed-plugin lookup is supplied as a fixed fake set via the internal delegate constructor, since
/// <c>PluginBootstrap</c> is sealed and would otherwise hit the real plugins folder on disk.
/// </summary>
public class PluginUpdateCheckerTests
{
    private const string StoreUrl = "https://example.test/store/index.json";

    [Fact]
    public async Task CheckNowAsync_NewerVersionInStore_TriggersOneToast()
    {
        var toastService = Substitute.For<IToastService>();
        var checker = _CreateChecker(
            installed: [_Plugin("youtrack", "YouTrack", "1.0.0")],
            storeClient: _StoreClientReturning(_Entry("youtrack", "1.1.0")),
            toastService: toastService);

        await checker.CheckNowAsync();

        toastService.Received(1).Show(
            Arg.Is<string>(message => message.Contains("YouTrack") && message.Contains("1.0.0") && message.Contains("1.1.0")),
            ToastSeverity.Information,
            Arg.Any<string?>(),
            Arg.Any<Action?>());
    }

    [Fact]
    public async Task CheckNowAsync_NoNewerVersion_NoToast()
    {
        var toastService = Substitute.For<IToastService>();
        var checker = _CreateChecker(
            installed: [_Plugin("youtrack", "YouTrack", "1.1.0")],
            storeClient: _StoreClientReturning(_Entry("youtrack", "1.1.0")),
            toastService: toastService);

        await checker.CheckNowAsync();

        toastService.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task CheckNowAsync_PluginNotInstalled_NoToast()
    {
        var toastService = Substitute.For<IToastService>();
        var checker = _CreateChecker(
            installed: [],
            storeClient: _StoreClientReturning(_Entry("youtrack", "1.1.0")),
            toastService: toastService);

        await checker.CheckNowAsync();

        toastService.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task CheckNowAsync_StoreUnreachable_NoToastAndDoesNotThrow()
    {
        var toastService = Substitute.For<IToastService>();
        var storeClient = Substitute.For<IPluginStoreClient>();
        storeClient.FetchIndexAsync(StoreUrl, Arg.Any<CancellationToken>())
            .Returns(new PluginStoreFetchResult(false, "unreachable", null, null));
        var checker = _CreateChecker(
            installed: [_Plugin("youtrack", "YouTrack", "1.0.0")],
            storeClient: storeClient,
            toastService: toastService);

        await checker.CheckNowAsync();

        toastService.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task CheckNowAsync_InstalledLookupThrows_NoToastAndDoesNotThrow()
    {
        var toastService = Substitute.For<IToastService>();
        var storeConfigStore = Substitute.For<IPluginStoreConfigStore>();
        storeConfigStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([StoreUrl]);
        var checker = new PluginUpdateChecker(
            _ => throw new IOException("plugins folder unavailable"),
            storeConfigStore,
            _StoreClientReturning(_Entry("youtrack", "1.1.0")),
            toastService,
            TestCockpit.NewViewModel(),
            NullLogger<PluginUpdateChecker>.Instance);

        var act = () => checker.CheckNowAsync();

        await act.Should().NotThrowAsync();
        toastService.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task CheckNowAsync_CalledTwiceForTheSameUpdate_OnlyTriggersOneToast()
    {
        var toastService = Substitute.For<IToastService>();
        var checker = _CreateChecker(
            installed: [_Plugin("youtrack", "YouTrack", "1.0.0")],
            storeClient: _StoreClientReturning(_Entry("youtrack", "1.1.0")),
            toastService: toastService);

        await checker.CheckNowAsync();
        await checker.CheckNowAsync();

        toastService.Received(1).Show(Arg.Any<string>(), ToastSeverity.Information, Arg.Any<string?>(), Arg.Any<Action?>());
    }

    [Fact]
    public async Task CheckNowAsync_TwoPluginsUpdated_AggregatesIntoOneToast()
    {
        var toastService = Substitute.For<IToastService>();
        var storeClient = Substitute.For<IPluginStoreClient>();
        storeClient.FetchIndexAsync(StoreUrl, Arg.Any<CancellationToken>())
            .Returns(new PluginStoreFetchResult(
                true,
                null,
                new PluginStoreIndex(null, [_Entry("youtrack", "1.1.0"), _Entry("github-issues", "2.0.0")]),
                StoreUrl));
        var checker = _CreateChecker(
            installed:
            [
                _Plugin("youtrack", "YouTrack", "1.0.0"),
                _Plugin("github-issues", "GitHub Issues", "1.5.0"),
            ],
            storeClient: storeClient,
            toastService: toastService);

        await checker.CheckNowAsync();

        toastService.Received(1).Show(
            Arg.Is<string>(message => message.Contains("2 plugin updates")),
            ToastSeverity.Information,
            Arg.Any<string?>(),
            Arg.Any<Action?>());
    }

    private static PluginUpdateChecker _CreateChecker(
        IReadOnlyList<DiscoveredPlugin> installed,
        IPluginStoreClient storeClient,
        IToastService toastService)
    {
        var storeConfigStore = Substitute.For<IPluginStoreConfigStore>();
        storeConfigStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([StoreUrl]);

        return new PluginUpdateChecker(
            _ => Task.FromResult(installed),
            storeConfigStore,
            storeClient,
            toastService,
            TestCockpit.NewViewModel(),
            NullLogger<PluginUpdateChecker>.Instance);
    }

    private static IPluginStoreClient _StoreClientReturning(PluginStoreEntry entry)
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        storeClient.FetchIndexAsync(StoreUrl, Arg.Any<CancellationToken>())
            .Returns(new PluginStoreFetchResult(true, null, new PluginStoreIndex(null, [entry]), StoreUrl));
        return storeClient;
    }

    private static PluginStoreEntry _Entry(string id, string latestVersion) =>
        new(id, id, null, null, latestVersion, []);

    private static DiscoveredPlugin _Plugin(string folderId, string name, string version) =>
        new(
            FolderPath: $"/plugins/{folderId}",
            FolderId: folderId,
            Manifest: new PluginManifest(folderId, name, version, "Plugin.dll", 1, null, null, null, null),
            Sha256: "deadbeef",
            Decision: PluginLoadDecision.Load);
}
