using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels.Onboarding;

/// <summary>
/// The first-run wizard's provider step (AC-510[b]): offline is its own honest state (criterion 3), the store's
/// category axis is what filters the catalogue down to providers (criterion 5), and installing goes through the
/// batch provisioning call so a mixed result shows as "half succeeded" rather than one opaque failure
/// (criterion 2). <see cref="ProviderPickerRowViewModelTests"/> covers the per-row rendering of each outcome;
/// this covers the step wiring them together.
/// </summary>
public class ProviderStepViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-provider-step-tests", Guid.NewGuid().ToString("N"));

    private PluginBootstrap _EmptyBootstrap() =>
        new(Path.Combine(_tempDir, "plugins"), Path.Combine(_tempDir, "cockpit.json"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParameterlessConstructor_IsInertAndNotLoading()
    {
        var vm = new ProviderStepViewModel();

        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Providers);
        Assert.False(vm.IsOffline);
    }

    // --- Criterion 3: offline is every configured store failing, and it is a plain fact, not styled as an error
    // — the local-providers note is unaffected either way. --------------------------------------------------------

    [Fact]
    public async Task LoadAsync_TheOnlyConfiguredStoreIsUnreachable_SetsOffline_WithItsOwnErrorCarried()
    {
        var configStore = Substitute.For<IPluginStoreConfigStore>();
        var store = PluginStoreConfig.Remote("https://example.invalid/index.json");
        configStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([store]);
        var storeClient = Substitute.For<IPluginStoreClient>();
        storeClient.FetchIndexAsync(store, Arg.Any<CancellationToken>())
            .Returns(new PluginStoreFetchResult(false, "No such host is known.", null, null));

        var vm = new ProviderStepViewModel(configStore, storeClient, Substitute.For<IPluginProvisioningService>(), _EmptyBootstrap());
        await vm.LoadAsync();

        Assert.True(vm.IsOffline);
        Assert.Equal("No such host is known.", vm.OfflineMessage);
        Assert.Empty(vm.Providers);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task LoadAsync_NoStoresConfiguredAtAll_IsNotOffline_JustEmpty()
    {
        var configStore = Substitute.For<IPluginStoreConfigStore>();
        configStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        var vm = new ProviderStepViewModel(configStore, Substitute.For<IPluginStoreClient>(), Substitute.For<IPluginProvisioningService>(), _EmptyBootstrap());
        await vm.LoadAsync();

        // No stores configured is a different, honest state from every store having failed to answer.
        Assert.False(vm.IsOffline);
        Assert.False(vm.HasProviders);
    }

    [Fact]
    public void LocalProvidersText_NamesOllamaAndLmStudio_TheCoreAlwaysAvailableProviders()
    {
        var vm = new ProviderStepViewModel();

        Assert.Contains("Ollama", vm.LocalProvidersText, StringComparison.Ordinal);
        Assert.Contains("LM Studio", vm.LocalProvidersText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The doc comment on <see cref="ProviderStepViewModel.LoadAsync"/> claims the latest call always wins over
    /// an older one still in flight — this is what makes that literally true rather than merely usually true. The
    /// constructor itself fires the first (fire-and-forget) run; it is made the stale, slow one here, and a second
    /// explicit call finishes first — the slow run's own delayed continuation, once released, must not then
    /// overwrite what the second one already wrote.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ASecondCallWhileTheFirstIsStillInFlight_WinsOverTheStaleOne()
    {
        var slowStore = PluginStoreConfig.Remote("https://slow.example/index.json");
        var fastStore = PluginStoreConfig.Remote("https://fast.example/index.json");
        var storeClient = Substitute.For<IPluginStoreClient>();
        var releaseSlowFetch = new TaskCompletionSource();
        var slowFetchStarted = new TaskCompletionSource();
        storeClient.FetchIndexAsync(slowStore, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            slowFetchStarted.TrySetResult();
            await releaseSlowFetch.Task;
            // If this stale run is not superseded, it would (wrongly) land as the final state.
            return new PluginStoreFetchResult(false, "the slow store timed out", null, null);
        });
        var fastIndexEntry = new PluginStoreEntry(
            "gemini-provider", "Gemini", "d", "Cockpit", "1.0.0", [], Category: PluginStoreEntry.ProviderCategory);
        storeClient.FetchIndexAsync(fastStore, Arg.Any<CancellationToken>())
            .Returns(new PluginStoreFetchResult(true, null, new PluginStoreIndex("Fast store", [fastIndexEntry]), "https://fast.example/index.json"));

        var storeConfigStore = Substitute.For<IPluginStoreConfigStore>();
        // Call 1 (the constructor's own fire-and-forget LoadAsync) sees the slow store; call 2 (this test's
        // explicit LoadAsync) sees the fast one.
        storeConfigStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([slowStore], [fastStore]);

        var vm = new ProviderStepViewModel(storeConfigStore, storeClient, Substitute.For<IPluginProvisioningService>(), _EmptyBootstrap());
        await slowFetchStarted.Task; // the constructor's own run is now parked inside the slow fetch

        await vm.LoadAsync(); // the second, fast call — completes fully before the slow one is released

        releaseSlowFetch.TrySetResult();
        await Task.Delay(200); // lets the slow run's continuation actually resume and hit the generation check

        // The second call's result stands: one provider from the fast store, not offline — the slow run's
        // "unreachable" verdict from a superseded generation never landed.
        Assert.False(vm.IsOffline);
        Assert.Single(vm.Providers);
        Assert.Equal("gemini-provider", vm.Providers[0].Row.Id);
    }

    // --- Criterion 5, wired end to end: the real deserializer parses a fixture shaped like the live index, and
    // the category filter keeps only the AI-provider entries (repo-valkuil #5: not a hand-built list of records). --

    [Fact]
    public async Task LoadAsync_RealStoreIndex_KeepsOnlyTheProviderCategoryEntries()
    {
        var storeDir = Path.Combine(_tempDir, "store");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "index.json"), """
        {
          "name": "AI-Cockpit Plugins",
          "plugins": [
            { "id": "git-status", "name": "Git status", "latestVersion": "1.0.0", "category": "Productivity", "versions": [] },
            { "id": "gemini-provider", "name": "Gemini / OpenAI Provider", "latestVersion": "0.1.2", "category": "AI providers",
              "versions": [ { "version": "0.1.2", "path": "gemini-provider-0.1.2.zip", "abstractionsVersion": 1 } ] }
          ]
        }
        """);
        var store = PluginStoreConfig.Local(storeDir);
        var configStore = Substitute.For<IPluginStoreConfigStore>();
        configStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([store]);

        var vm = new ProviderStepViewModel(configStore, new PluginStoreClient(), Substitute.For<IPluginProvisioningService>(), _EmptyBootstrap());
        await vm.LoadAsync();

        Assert.False(vm.IsOffline);
        Assert.Single(vm.Providers);
        Assert.Equal("gemini-provider", vm.Providers[0].Row.Id);
        // gemini-provider is a cloud endpoint (ProviderHostExecutables has no CLI for it) — deterministic
        // regardless of what happens to be on the test machine's own PATH.
        Assert.Equal(ProviderDetectionState.NotApplicable, vm.Providers[0].Detection);
    }

    // --- Criterion 2, "half succeeded": one plugin failing in the batch is isolated, and the summary names it
    // rather than reading as one opaque failure. --------------------------------------------------------------------

    [Fact]
    public async Task InstallSelectedAsync_MixedBatchResult_AppliesEachRowsOwnOutcome_AndSummarisesPartialSuccess()
    {
        var provisioningService = Substitute.For<IPluginProvisioningService>();
        var vm = new ProviderStepViewModel(
            Substitute.For<IPluginStoreConfigStore>(), Substitute.For<IPluginStoreClient>(), provisioningService, _EmptyBootstrap());
        var okRow = _SelectableRow("alpha");
        var failRow = _SelectableRow("beta");
        vm.Providers.Add(okRow);
        vm.Providers.Add(failRow);
        okRow.IsSelected = true;
        failRow.IsSelected = true;

        provisioningService
            .InstallManyAsync(Arg.Any<IReadOnlyList<PluginProvisionRequest>>(), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>())
            .Returns(new PluginProvisionBatchResult(
            [
                new PluginProvisionResult(PluginProvisionOutcome.Installed, "alpha", "Alpha", null, null, "alpha", "sha"),
                new PluginProvisionResult(PluginProvisionOutcome.Failed, "beta", "Beta", "the store went away", null, null, null),
            ]));

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.True(okRow.HasOutcome);
        Assert.True(failRow.HasOutcome);
        Assert.Contains("plugin store", okRow.OutcomeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the store went away", failRow.OutcomeText, StringComparison.Ordinal);
        Assert.Contains("1 of 2", vm.SummaryMessage, StringComparison.Ordinal);
        Assert.Contains("Beta", vm.SummaryMessage, StringComparison.Ordinal);
        Assert.False(vm.IsInstalling);
    }

    [Fact]
    public async Task InstallSelectedAsync_NothingSelected_NeverCallsTheProvisioningService()
    {
        var provisioningService = Substitute.For<IPluginProvisioningService>();
        var vm = new ProviderStepViewModel(
            Substitute.For<IPluginStoreConfigStore>(), Substitute.For<IPluginStoreClient>(), provisioningService, _EmptyBootstrap());
        vm.Providers.Add(_SelectableRow("alpha")); // left unselected

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        await provisioningService.DidNotReceive().InstallManyAsync(
            Arg.Any<IReadOnlyList<PluginProvisionRequest>>(), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>());
        Assert.Equal(string.Empty, vm.SummaryMessage);
    }

    [Fact]
    public void CanInstallSelected_FollowsWhetherAnyRowIsChecked()
    {
        var vm = new ProviderStepViewModel(
            Substitute.For<IPluginStoreConfigStore>(), Substitute.For<IPluginStoreClient>(), Substitute.For<IPluginProvisioningService>(), _EmptyBootstrap());
        var row = _SelectableRow("alpha");
        vm.Providers.Add(row);
        Assert.False(vm.CanInstallSelected);

        row.IsSelected = true;
        Assert.True(vm.CanInstallSelected);

        row.IsSelected = false;
        Assert.False(vm.CanInstallSelected);
    }

    private static ProviderPickerRowViewModel _SelectableRow(string id)
    {
        var entry = new PluginStoreEntry(
            id, id, "d", "Cockpit", "1.0.0",
            [new PluginStoreVersion("1.0.0", $"{id}-1.0.0.zip", 1, null, null, null)],
            Category: PluginStoreEntry.ProviderCategory);
        var row = new Cockpit.App.ViewModels.StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://example.com/index.json"), installedVersion: null);

        return new ProviderPickerRowViewModel(row, ProviderDetectionState.NotApplicable);
    }
}
