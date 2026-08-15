using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-815: a store entry with Hidden set drops out of the browsable catalogue entirely.
/// </summary>
public class PluginManagerViewModelHiddenFilterTests
{
    [Fact]
    public async Task BrowseStoresAsync_SkipsHiddenEntries_ButKeepsTheRest()
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        var registrationStore = Substitute.For<IPluginRegistrationStore>();
        registrationStore
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(new Dictionary<string, PluginRegistration>()));

        var hidden = new PluginStoreEntry(
            "example-workspace", "Example Workspace", null, "Cockpit", "1.0.0",
            [new PluginStoreVersion("1.0.0", "example-workspace/ex-1.0.0.zip", null, null, null, null)],
            Hidden: true);
        var visible = new PluginStoreEntry(
            "github-issues", "GitHub Issues", null, "Cockpit", "1.0.0",
            [new PluginStoreVersion("1.0.0", "github-issues/gh-1.0.0.zip", null, null, null, null)]);

        storeClient.FetchIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PluginStoreFetchResult(true, null, new PluginStoreIndex("My Store", [hidden, visible]), "https://store.example/index.json")));

        var manager = new PluginManagerViewModel(
            registrationStore,
            Substitute.For<IPluginInstaller>(),
            new PluginBootstrap(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IPluginStoreConfigStore>(),
            storeClient,
            new Dictionary<string, PluginSettingsRegistration>(),
            new PluginDiagnostics());
        manager.Stores.Add(PluginStoreConfig.Remote("https://store.example/index.json"));

        await manager.BrowseStoresAsync();

        Assert.DoesNotContain(manager.AvailablePlugins, row => row.Id == "example-workspace");
        Assert.Contains(manager.AvailablePlugins, row => row.Id == "github-issues");
    }
}
