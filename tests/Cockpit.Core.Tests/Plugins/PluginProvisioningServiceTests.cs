using System.IO.Compression;
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
        _service = new PluginProvisioningService(_storeClient, _installer);
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

    [Fact]
    public async Task InstallAsync_AVersionFoundByParsingARealIndex_Installs()
    {
        var zipBytes = _WritePluginZip("acme", "1.0.0", "MZ-acme-v1");
        var sha = PluginHash.Compute(zipBytes);
        _WriteIndex($$"""
            {
              "name": "Acme store",
              "plugins": [
                {
                  "id": "acme",
                  "name": "Acme",
                  "description": "d",
                  "author": "me",
                  "latestVersion": "1.0.0",
                  "versions": [
                    { "version": "1.0.0", "path": "acme-1.0.0.zip", "abstractionsVersion": 1, "minHostVersion": null, "sha256": "{{sha}}", "notes": null }
                  ]
                }
              ]
            }
            """);
        var store = PluginStoreConfig.Local(_storeDir);
        var fetch = await _storeClient.FetchIndexAsync(store);
        Assert.True(fetch.IsSuccess);
        var version = fetch.Index!.Plugins.Single().Versions[0];

        var result = await _service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, version), HostMajor, HostVersion);

        Assert.Equal(PluginProvisionOutcome.Installed, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal("acme", result.FolderId);
        Assert.True(File.Exists(Path.Combine(_pluginsRoot, "acme", "plugin.json")));
    }

    [Fact]
    public async Task FetchIndexAsync_CorruptJson_FailsWithoutThrowing()
    {
        _WriteIndex("{ this is not valid json");

        var result = await _storeClient.FetchIndexAsync(PluginStoreConfig.Local(_storeDir));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task FetchIndexAsync_EmptyIndex_ParsesAsAnEmptyCatalogue_RatherThanFailing()
    {
        _WriteIndex("{}");

        var result = await _storeClient.FetchIndexAsync(PluginStoreConfig.Local(_storeDir));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Index!.Plugins);
    }

    [Fact]
    public async Task FetchIndexAsync_StoreUnreachable_FailsWithoutThrowing()
    {
        var missing = Path.Combine(_tempDir, "no-such-store");

        var result = await _storeClient.FetchIndexAsync(PluginStoreConfig.Local(missing));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    // --- Checksum (criterion 3, unchanged behaviour, pinned through the service): mismatch is a hard rejection,
    // a missing hash is a warning that does not block the install. ------------------------------------------------

    [Fact]
    public async Task InstallAsync_ChecksumMismatch_IsAHardRejection_AndNothingLandsOnDisk()
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
    public async Task InstallAsync_MissingChecksum_WarnsButStillInstalls()
    {
        _WritePluginZip("acme", "1.0.0", "MZ-acme-v1");
        var store = PluginStoreConfig.Local(_storeDir);
        var version = new PluginStoreVersion("1.0.0", "acme-1.0.0.zip", 1, null, Sha256: null, null);

        var result = await _service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, version), HostMajor, HostVersion);

        Assert.Equal(PluginProvisionOutcome.Installed, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Warning);
        Assert.Contains("checksum", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    // --- Incompatible (criterion 2 + AC-181): refused before anything is fetched — proven by there being no zip
    // on disk to download at all; a Failed (not Incompatible) outcome here would mean it tried anyway. -----------

    [Fact]
    public async Task InstallAsync_IncompatibleMinHostVersion_RefusesWithoutDownloading()
    {
        var store = PluginStoreConfig.Local(_storeDir);
        var version = new PluginStoreVersion("2.0.0", "acme-2.0.0.zip", 1, "9.0.0", null, null);

        var result = await _service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, version), HostMajor, HostVersion);

        Assert.Equal(PluginProvisionOutcome.Incompatible, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("9.0.0", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_IncompatibleAbstractionsMajor_RefusesWithoutDownloading()
    {
        var store = PluginStoreConfig.Local(_storeDir);
        var version = new PluginStoreVersion("2.0.0", "acme-2.0.0.zip", 2, null, null, null);

        var result = await _service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, version), HostMajor, HostVersion);

        Assert.Equal(PluginProvisionOutcome.Incompatible, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Contains("contract version 2", result.Error, StringComparison.Ordinal);
    }

    // --- Already installed (criterion 2): a second install over the same folder id stages under
    // .pending-updates rather than overwriting the live copy. -----------------------------------------------------

    [Fact]
    public async Task InstallAsync_OverAnExistingInstall_StagesInsteadOfReplacingTheLiveCopy()
    {
        var store = PluginStoreConfig.Local(_storeDir);
        _WritePluginZip("acme", "1.0.0", "MZ-acme-v1");
        var v1 = new PluginStoreVersion("1.0.0", "acme-1.0.0.zip", 1, null, null, null);
        var first = await _service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, v1), HostMajor, HostVersion);
        Assert.Equal(PluginProvisionOutcome.Installed, first.Outcome);

        _WritePluginZip("acme", "2.0.0", "MZ-acme-v2");
        var v2 = new PluginStoreVersion("2.0.0", "acme-2.0.0.zip", 1, null, null, null);
        var second = await _service.InstallAsync(new PluginProvisionRequest("acme", "Acme", store, v2), HostMajor, HostVersion);

        Assert.Equal(PluginProvisionOutcome.Staged, second.Outcome);
        Assert.True(second.IsSuccess);
        // The live install is untouched (may be loaded/locked); the new bytes wait for the next restart.
        Assert.Equal("MZ-acme-v1", await File.ReadAllTextAsync(Path.Combine(_pluginsRoot, "acme", "Plugin.dll")));
        Assert.Equal("MZ-acme-v2", await File.ReadAllTextAsync(Path.Combine(_pluginsRoot, ".pending-updates", "acme", "Plugin.dll")));
    }

    // --- Batch (criterion 2, "half gelukt"): one request failing is isolated, the rest still land, and the batch
    // result names which one did not. --------------------------------------------------------------------------

    [Fact]
    public async Task InstallManyAsync_TheMiddleRequestFails_TheOthersStillInstall()
    {
        var store = PluginStoreConfig.Local(_storeDir);
        _WritePluginZip("alpha", "1.0.0", "MZ-alpha");
        _WritePluginZip("gamma", "1.0.0", "MZ-gamma");
        // "beta" has no zip on disk at all, so its download fails — the middle request of three.
        PluginProvisionRequest[] requests =
        [
            new("alpha", "Alpha", store, new PluginStoreVersion("1.0.0", "alpha-1.0.0.zip", 1, null, null, null)),
            new("beta", "Beta", store, new PluginStoreVersion("1.0.0", "beta-1.0.0.zip", 1, null, null, null)),
            new("gamma", "Gamma", store, new PluginStoreVersion("1.0.0", "gamma-1.0.0.zip", 1, null, null, null)),
        ];

        var batch = await _service.InstallManyAsync(requests, HostMajor, HostVersion);

        Assert.Equal(3, batch.Results.Count);
        Assert.Equal(2, batch.SucceededCount);
        Assert.Equal(["Beta"], batch.FailedNames);
        Assert.Equal(PluginProvisionOutcome.Installed, batch.Results[0].Outcome);
        Assert.Equal(PluginProvisionOutcome.Failed, batch.Results[1].Outcome);
        Assert.Equal(PluginProvisionOutcome.Installed, batch.Results[2].Outcome);
        Assert.True(Directory.Exists(Path.Combine(_pluginsRoot, "alpha")));
        Assert.True(Directory.Exists(Path.Combine(_pluginsRoot, "gamma")));
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "beta")));
    }

    // The real PluginStoreClient/PluginInstaller never throw (both wrap their own IO in try/catch), so isolation
    // against a genuine throw — not just a returned failure — needs a store client that actually does.
    [Fact]
    public async Task InstallManyAsync_ARequestThatThrows_IsIsolated_TheRestStillLand()
    {
        _WritePluginZip("alpha", "1.0.0", "MZ-alpha");
        _WritePluginZip("gamma", "1.0.0", "MZ-gamma");
        var attempt = 0;
        var throwingStore = Substitute.For<IPluginStoreClient>();
        throwingStore
            .DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (++attempt == 2)
                {
                    throw new IOException("the store went away mid-download");
                }

                var zipPath = Path.Combine(_storeDir, callInfo.ArgAt<string>(1));
                return Task.FromResult(new PluginStoreDownloadResult(true, null, zipPath));
            });
        var service = new PluginProvisioningService(throwingStore, _installer);
        var store = PluginStoreConfig.Local(_storeDir);
        PluginProvisionRequest[] requests =
        [
            new("alpha", "Alpha", store, new PluginStoreVersion("1.0.0", "alpha-1.0.0.zip", 1, null, null, null)),
            new("beta", "Beta", store, new PluginStoreVersion("1.0.0", "beta-1.0.0.zip", 1, null, null, null)),
            new("gamma", "Gamma", store, new PluginStoreVersion("1.0.0", "gamma-1.0.0.zip", 1, null, null, null)),
        ];

        var batch = await service.InstallManyAsync(requests, HostMajor, HostVersion);

        Assert.Equal(3, batch.Results.Count);
        Assert.Equal(2, batch.SucceededCount);
        Assert.Equal(["Beta"], batch.FailedNames);
        Assert.Equal(PluginProvisionOutcome.Failed, batch.Results[1].Outcome);
        Assert.NotNull(batch.Results[1].Error);
        Assert.Contains("went away", batch.Results[1].Error, StringComparison.Ordinal);
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
