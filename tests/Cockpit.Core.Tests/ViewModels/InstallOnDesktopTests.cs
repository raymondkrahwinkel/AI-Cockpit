using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Core.Projects;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-1590: "Install on this desktop" for a plugin the server runs and this desktop lacks. The plugin comes from this
/// desktop's own store through the local install, and nothing of the server but the plugin's id takes part.
/// </summary>
public class InstallOnDesktopTests
{
    private static readonly PluginStoreConfig _LocalStore = PluginStoreConfig.Remote("https://desktop-store.example/index.json");

    // A plugin the desktop store has is installed from that store and asks for a restart; one no store has keeps its
    // button, asks for no restart, and says why.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheButton_InstallsFromTheDesktopStore_OrSaysWhyItCannot(bool storeHasIt)
    {
        var local = _LocalAdministration(_Entry(storeHasIt ? "depot" : "something-else"));
        var server = Substitute.For<IPluginAdministration>();
        var manager = _Manager(local);
        var admin = new ServerAdminViewModel("homelab", "key", Substitute.For<IConnectKeyAdministration>(), [], [], server)
        {
            InstallPluginOnDesktop = row => manager.InstallFromStoreByIdAsync(row.Id),
        };
        var row = new ServerPluginRowViewModel(_ServerPlugin("depot")) { OnDesktop = false };

        await admin.InstallPluginOnDesktopCommand.ExecuteAsync(row);

        // The server's side never takes part, nor does the zip route.
        Assert.Empty(server.ReceivedCalls());
        await local.DidNotReceive().InstallFromZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(!storeHasIt, row.MissingOnDesktop);
        Assert.Equal(storeHasIt, admin.DesktopNeedsRestart);
        Assert.Equal(storeHasIt ? 1 : 0, local.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IPluginAdministration.InstallFromStoreAsync)));
        if (storeHasIt)
        {
            await local.Received(1).InstallFromStoreAsync(
                Arg.Is<PluginProvisionRequest>(request => request.Id == "depot" && request.Store.SameStoreAs(_LocalStore)),
                Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.Contains("'depot' is not in any of this desktop's plugin stores", admin.Status);
        }
    }

    // AC-1986: connecting Depot in the plugin's settings window shows its projects without reopening the page.
    [Fact]
    public async Task OpeningPluginSettings_ReloadsTheSharedProjects()
    {
        var projects = Substitute.For<IServerProjects>();
        projects.Current.Returns(ProjectCatalogSnapshot.Empty);
        var shared = new AssistantSharedProjectRow("p1", "Shared", null, null);
        projects.ListSharedAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<AssistantSharedProjectSourceRow>>([]),
            Task.FromResult<IReadOnlyList<AssistantSharedProjectSourceRow>>([new("Depot", true, null, [shared])]));
        var admin = new ServerAdminViewModel("homelab", "key", Substitute.For<IConnectKeyAdministration>(), [], [], Substitute.For<IPluginAdministration>(), projects)
        {
            OpenPluginSettings = _ => Task.CompletedTask,
        };
        var row = new ServerPluginRowViewModel(_ServerPlugin("depot"));
        await admin.OpenPluginSettingsCommand.ExecuteAsync(row);
        Assert.Single(admin.SharedNotices);

        await admin.OpenPluginSettingsCommand.ExecuteAsync(row);

        Assert.Equal("Shared", Assert.Single(admin.SharedProjects).Name);
        Assert.Empty(admin.SharedNotices);
    }

    private static PluginManagerViewModel _Manager(IPluginAdministration local) =>
        new(local, Substitute.For<ISessionDialogService>(), [_LocalStore]);

    private static IPluginAdministration _LocalAdministration(params PluginStoreEntry[] entries)
    {
        var local = Substitute.For<IPluginAdministration>();
        local.GetInstalledAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InstalledPlugin>>([]));
        local.FetchStoreIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PluginStoreFetchResult(true, null, new PluginStoreIndex("Desktop store", entries), "https://desktop-store.example/index.json")));
        local.InstallFromStoreAsync(Arg.Any<PluginProvisionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<PluginProvisionRequest>();
                return Task.FromResult(new PluginProvisionResult(PluginProvisionOutcome.Installed, request.Id, request.Name, null, null, request.Id, "sha"));
            });
        return local;
    }

    private static PluginStoreEntry _Entry(string id) =>
        new(id, id, null, "Cockpit", "1.0.0", [new PluginStoreVersion("1.0.0", $"plugins/{id}-1.0.0.zip", null, null, null, null)]);

    private static InstalledPlugin _ServerPlugin(string id) =>
        new(new DiscoveredPlugin($"C:\\server\\plugins\\{id}", id, new PluginManifest(id, id, "1.0.0", null, 0, null, null, null, null), "server-hash", PluginLoadDecision.Load),
            new PluginRegistration(true, "server-hash"), null, null, null);
}
