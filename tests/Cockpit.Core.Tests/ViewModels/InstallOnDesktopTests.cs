using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-1590: "Install on this desktop" for a plugin the server runs and this desktop lacks. The plugin comes from this
/// desktop's own store through the local install, and nothing of the server but the plugin's id takes part.
/// </summary>
public class InstallOnDesktopTests
{
    private static readonly PluginStoreConfig _LocalStore = PluginStoreConfig.Remote("https://desktop-store.example/index.json");

    [Fact]
    public async Task APluginInTheDesktopStore_IsInstalledFromThatStore_AndAsksForARestart()
    {
        var local = _LocalAdministration(_Entry("depot"));

        var outcome = await _Manager(local).InstallFromStoreByIdAsync("depot");

        Assert.True(outcome.Installed);
        Assert.True(outcome.NeedsRestart);
        await local.Received(1).InstallFromStoreAsync(
            Arg.Is<PluginProvisionRequest>(request => request.Id == "depot" && request.Store.SameStoreAs(_LocalStore)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APluginNoDesktopStoreHas_IsNotInstalled_AndSaysWhy()
    {
        var local = _LocalAdministration(_Entry("something-else"));

        var outcome = await _Manager(local).InstallFromStoreByIdAsync("depot");

        Assert.False(outcome.Installed);
        Assert.False(outcome.NeedsRestart);
        Assert.Contains("'depot' is not in any of this desktop's plugin stores", outcome.Message);
        await local.DidNotReceive().InstallFromStoreAsync(Arg.Any<PluginProvisionRequest>(), Arg.Any<CancellationToken>());
    }

    // The tegenproef: there is no way for the server's side to take part. The server's administration is the only
    // thing a row reaches the server by, and pressing the button must not touch it, nor the zip route.
    [Fact]
    public async Task TheButton_NeverCallsTheServer_AndNeverInstallsFromAZip()
    {
        var local = _LocalAdministration(_Entry("depot"));
        var server = Substitute.For<IPluginAdministration>();
        var manager = _Manager(local);
        var admin = new ServerAdminViewModel("homelab", "key", Substitute.For<IConnectKeyAdministration>(), [], [], server)
        {
            IsPluginOnDesktop = _ => false,
            InstallPluginOnDesktop = row => manager.InstallFromStoreByIdAsync(row.Id),
        };
        var row = new ServerPluginRowViewModel(_ServerPlugin("depot"));

        await admin.InstallPluginOnDesktopCommand.ExecuteAsync(row);

        Assert.Empty(server.ReceivedCalls());
        await local.DidNotReceive().InstallFromZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.True(admin.DesktopNeedsRestart);
        Assert.False(row.MissingOnDesktop);
    }

    [Fact]
    public async Task APluginThatCouldNotBeInstalled_KeepsItsButton_AndAsksForNoRestart()
    {
        var manager = _Manager(_LocalAdministration());
        var admin = new ServerAdminViewModel("homelab", "key", Substitute.For<IConnectKeyAdministration>(), [], [], Substitute.For<IPluginAdministration>())
        {
            InstallPluginOnDesktop = row => manager.InstallFromStoreByIdAsync(row.Id),
        };
        var row = new ServerPluginRowViewModel(_ServerPlugin("depot")) { OnDesktop = false };

        await admin.InstallPluginOnDesktopCommand.ExecuteAsync(row);

        Assert.True(row.MissingOnDesktop);
        Assert.False(admin.DesktopNeedsRestart);
        Assert.Contains("not in any", admin.Status);
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
