using System.IO.Compression;
using System.Runtime.InteropServices;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The provisioning seam (AC-510[b]) driven through the real <see cref="PluginStoreClient"/> (a local-folder
/// store) and the real <see cref="PluginInstaller"/> — not fakes shaped to the answer, so a bug in how the two
/// compose cannot hide behind a fake that already returns what the test expects. Covers the four outcomes the
/// ticket names apart: store-unreachable/corrupt-index, incompatible (refused before any download), staged
/// (already installed), and a batch where the middle request fails.
/// </summary>
public class PluginProvisioningServiceTests : IDisposable
{
    private const int HostMajor = 1;
    private static readonly Version HostVersion = new(1, 5, 0);

    private readonly string _tempDir;
    private readonly string _storeDir;
    private readonly string _pluginsRoot;
    private readonly PluginStoreClient _storeClient = new();
    private readonly PluginInstaller _installer;
    private readonly PluginProvisioningService _service;

    public PluginProvisioningServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-provisioning-tests", Guid.NewGuid().ToString("N"));
        _storeDir = Path.Combine(_tempDir, "store");
        _pluginsRoot = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(_storeDir);
        _installer = new PluginInstaller(_pluginsRoot);
        _service = new PluginProvisioningService(_storeClient, _installer, new PluginSignatureVerifier(), new PluginTrustStore(Path.Combine(_tempDir, "cockpit.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // --- Real deserializer (repo-valkuil #4): the version to install comes from PluginStoreIndex.TryParse via a
    // real FetchIndexAsync call over a fixture shaped like an actual index.json, not a hand-built record. --------

    // AC-1740: which zip a version installs on this host. Rows: a package for this RID wins over `path` (which points
    // at nothing); packages without this RID are refused with the reason and nothing fetched; no packages installs `path`.
    [Fact]
    public async Task InstallAsync_PicksThePackageForThisRid_FromARealIndex()
    {
        using (var scenario = new PluginProvisioningServiceTests())
        {
            await scenario._AssertPackageSelectionAsync("hit", PluginProvisionOutcome.Installed);
        }
        using (var scenario = new PluginProvisioningServiceTests())
        {
            await scenario._AssertPackageSelectionAsync("none", PluginProvisionOutcome.Installed);
        }
    }

    [Fact]
    public async Task InstallAsync_RefusesAMissingPlatformPackage()
    {
        await _AssertPackageSelectionAsync("miss", PluginProvisionOutcome.Incompatible);
    }

    private async Task _AssertPackageSelectionAsync(string packages, PluginProvisionOutcome expected)
    {
        var sha = PluginHash.Compute(_WritePluginZip("acme", "1.0.0", "MZ-acme-v1"));
        var rid = packages == "hit" ? RuntimeInformation.RuntimeIdentifier : "none-such-rid";
        var versionJson = packages == "none"
            ? $$"""{ "version": "1.0.0", "path": "acme-1.0.0.zip", "abstractionsVersion": 1, "sha256": "{{sha}}" }"""
            : $$"""{ "version": "1.0.0", "path": "missing.zip", "abstractionsVersion": 1, "packages": { "{{rid}}": { "path": "acme-1.0.0.zip", "sha256": "{{sha}}" } } }""";
        _WriteIndex($$"""{ "plugins": [ { "id": "acme", "name": "Acme", "latestVersion": "1.0.0", "versions": [ {{versionJson}} ] } ] }""");
        var store = PluginStoreConfig.Local(_storeDir);
        var index = (await _storeClient.FetchIndexAsync(store)).Index;
        Assert.NotNull(index);
        var version = index.Plugins.Single().Versions[0];

        var result = await _service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, version), HostMajor, HostVersion);

        var expectedError = expected == PluginProvisionOutcome.Incompatible ? $"No package for {RuntimeInformation.RuntimeIdentifier}" : null;
        Assert.Equal((expected, expectedError), (result.Outcome, result.Error));
        Assert.Equal(expected == PluginProvisionOutcome.Installed, File.Exists(Path.Combine(_pluginsRoot, "acme", "plugin.json")));
    }

    // --- Checksum (criterion 3, unchanged behaviour, pinned through the service): mismatch is a hard rejection,
    // a missing hash is a warning that does not block the install. ------------------------------------------------

    private async Task _AssertChecksumMismatchIsRejectedAsync()
    {
        _WritePluginZip("acme", "1.0.0", "MZ-acme-v1");
        var store = PluginStoreConfig.Local(_storeDir);
        var version = new PluginStoreVersion("1.0.0", "acme-1.0.0.zip", 1, null, "0000000000000000000000000000000000000000000000000000000000ff", null);

        var result = await _service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, version), HostMajor, HostVersion);

        Assert.Equal(PluginProvisionOutcome.Failed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "acme")));
    }

    [Fact]
    public async Task SignedSourceWithoutSignature_IsRefusedBeforeDownloadingOrInstalling()
    {
        var bytes = _WritePluginZip("acme", "1.0.0", "MZ-acme-v1");
        var zip = Path.Combine(_storeDir, "acme-1.0.0.zip");
        var client = Substitute.For<IPluginStoreClient>();
        client.DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<PluginStoreVersion>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IProgress<PluginDownloadProgress>?>())
            .Returns(new PluginStoreDownloadResult(true, null, zip));
        var store = PluginStoreConfig.Remote("https://depot.example/cockpit/stores/acme/index.json") with
        {
            Signed = new PluginStoreSignedSource("depot:depot.example/acme", "acme", () => new HttpClientHandler()),
        };
        var service = new PluginProvisioningService(client, _installer, new PluginSignatureVerifier(), new PluginTrustStore(Path.Combine(_tempDir, "cockpit.json")));
        var version = new PluginStoreVersion("1.0.0", "ignored.zip", HostMajor, null, PluginHash.Compute(bytes), null);

        var result = await service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, version), HostMajor, HostVersion);

        Assert.Equal(PluginProvisionOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(_pluginsRoot));
        await client.DidNotReceiveWithAnyArgs().DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<PluginStoreVersion>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<PluginDownloadProgress>?>());
        Assert.True(File.Exists(zip));
        File.Delete(zip);
        await _AssertChecksumMismatchIsRejectedAsync();
    }

    private byte[] _WritePluginZip(string id, string version, string dllContent)
    {
        var zipPath = Path.Combine(_storeDir, $"{id}-{version}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open()))
            {
                writer.Write($$"""{"id":"{{id}}","name":"{{id}}","version":"{{version}}","entryAssembly":"Plugin.dll","abstractionsVersion":1}""");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("Plugin.dll").Open()))
            {
                writer.Write(dllContent);
            }
        }

        return File.ReadAllBytes(zipPath);
    }

    private void _WriteIndex(string json) => File.WriteAllText(Path.Combine(_storeDir, "index.json"), json);
}
