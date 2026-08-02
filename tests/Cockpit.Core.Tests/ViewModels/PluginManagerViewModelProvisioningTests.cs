using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-510[b]'s "one install path, not two": when the composition root hands <c>PluginManagerViewModel</c> the
/// DI-resolved <see cref="IPluginProvisioningService"/> (the same instance the first-run wizard's provider step
/// receives — see <c>ProviderStepDependencyInjectionTests</c>), a store install goes through that exact instance
/// rather than a private one built from the store client/installer.
/// </summary>
public class PluginManagerViewModelProvisioningTests
{
    private static StorePluginRowViewModel _Row(string id = "acme", string name = "Acme")
    {
        var version = new PluginStoreVersion("1.0.0", $"{id}-1.0.0.zip", 1, null, null, null);
        var entry = new PluginStoreEntry(id, name, null, "Cockpit", "1.0.0", [version]);

        return new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://store.example/index.json"), installedVersion: null);
    }

    [Fact]
    public async Task InstallFromStoreAsync_WithAnInjectedProvisioningService_CallsThatExactInstance()
    {
        var provisioningService = Substitute.For<IPluginProvisioningService>();
        Func<NSubstitute.Core.CallInfo, PluginProvisionBatchResult> refuseBatchCall =
            _ => throw new InvalidOperationException("InstallFromStoreAsync should call InstallAsync (single), not the batch call.");
        provisioningService
            .InstallManyAsync(Arg.Any<IReadOnlyList<PluginProvisionRequest>>(), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>())
            .Returns(refuseBatchCall);
        provisioningService
            .InstallAsync(Arg.Any<PluginProvisionRequest>(), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>())
            .Returns(new PluginProvisionResult(PluginProvisionOutcome.Installed, "acme", "Acme", null, null, "acme", "sha"));

        var registrationStore = Substitute.For<IPluginRegistrationStore>();
        registrationStore.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(new Dictionary<string, PluginRegistration>()));

        // The store client/installer this manager also holds must never be reached for the install itself once a
        // provisioning service was handed in — DownloadZipAsync throwing proves the call never fell through to
        // building/using a private PluginProvisioningService(storeClient, installer) wrapping them instead.
        var storeClient = Substitute.For<IPluginStoreClient>();
        Func<NSubstitute.Core.CallInfo, PluginStoreDownloadResult> refuseDirectDownload =
            _ => throw new InvalidOperationException("Should not download directly — the injected provisioning service owns that.");
        storeClient
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(refuseDirectDownload);

        var manager = new PluginManagerViewModel(
            registrationStore,
            Substitute.For<IPluginInstaller>(),
            new PluginBootstrap(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IPluginStoreConfigStore>(),
            storeClient,
            new Dictionary<string, PluginSettingsRegistration>(),
            new PluginDiagnostics(),
            provisioningService: provisioningService);

        await manager.InstallFromStoreCommand.ExecuteAsync(_Row());

        await provisioningService.Received(1).InstallAsync(
            Arg.Is<PluginProvisionRequest>(request => request.Id == "acme"), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallFromStoreAsync_WithNoProvisioningServiceInjected_StillWorks_ByBuildingItsOwn()
    {
        // Backward compatibility (repo-valkuil #6-adjacent: an existing caller that never passes the new
        // parameter must keep behaving exactly as before) — proven end to end through the real store client and
        // installer, the same fixture shape PluginProvisioningServiceTests uses.
        var tempDir = Path.Combine(Path.GetTempPath(), "cockpit-manager-provisioning-fallback", Guid.NewGuid().ToString("N"));
        var storeDir = Path.Combine(tempDir, "store");
        var pluginsRoot = Path.Combine(tempDir, "plugins");
        Directory.CreateDirectory(storeDir);
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(Path.Combine(storeDir, "acme-1.0.0.zip"), System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var manifestWriter = new StreamWriter(archive.CreateEntry("plugin.json").Open()))
                {
                    manifestWriter.Write("""{"id":"acme","name":"acme","version":"1.0.0","entryAssembly":"Plugin.dll","abstractionsVersion":1}""");
                }

                using (var dllWriter = new StreamWriter(archive.CreateEntry("Plugin.dll").Open()))
                {
                    dllWriter.Write("MZ-acme");
                }
            }

            var installer = new PluginInstaller(pluginsRoot);
            var registrationStore = Substitute.For<IPluginRegistrationStore>();
            registrationStore.LoadAllAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyDictionary<string, PluginRegistration>>(new Dictionary<string, PluginRegistration>()));

            var manager = new PluginManagerViewModel(
                registrationStore,
                installer,
                new PluginBootstrap(pluginsRoot, Path.Combine(tempDir, "cockpit.json")),
                Substitute.For<ISessionDialogService>(),
                Substitute.For<IPluginStoreConfigStore>(),
                new PluginStoreClient(),
                new Dictionary<string, PluginSettingsRegistration>(),
                new PluginDiagnostics());

            var store = PluginStoreConfig.Local(storeDir);
            var version = new PluginStoreVersion("1.0.0", "acme-1.0.0.zip", 1, null, null, null);
            var entry = new PluginStoreEntry("acme", "Acme", null, "Cockpit", "1.0.0", [version]);
            var row = new StorePluginRowViewModel(entry, store, installedVersion: null);

            await manager.InstallFromStoreCommand.ExecuteAsync(row);

            Assert.True(Directory.Exists(Path.Combine(pluginsRoot, "acme")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
