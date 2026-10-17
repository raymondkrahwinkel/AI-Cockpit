using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Provisioning;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Infrastructure.Tests.Provisioning;

// AC-1506 (DB-2.5b): the applier's first phase, the one that overwrites cockpit.json. All or nothing up to that write,
// and "set", not "add": applying the same manifest again leaves the file byte for byte as it was.
public sealed class ProvisionManifestApplierTests : IDisposable
{
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"cockpit-apply-{Guid.NewGuid():N}");

    private string? _failingDownload;

    public ProvisionManifestApplierTests() => Directory.CreateDirectory(_stateRoot);

    public void Dispose() => Directory.Delete(_stateRoot, recursive: true);

    private string ConfigPath => Path.Combine(_stateRoot, "cockpit.json");

    private string PluginsRoot => Path.Combine(_stateRoot, "plugins");

    [Fact]
    public async Task A_failed_plugin_download_writes_nothing_and_names_the_plugin_then_the_manifest_applies_and_a_second_time_changes_nothing()
    {
        File.WriteAllText(ConfigPath, """{ "Profiles": [ { "Label": "Mine", "ConfigDir": "D:\\mine" } ], "Terminal": { "FontSize": 11 } }""");
        var before = File.ReadAllBytes(ConfigPath);

        _failingDownload = "beta";
        var failed = await _ApplyAsync();

        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Contains("plugins.beta", failed.Failure);
        Assert.Equal(["plugins.beta"], failed.Report.Where(line => line.Reason == ProvisionManifestApplier.PluginFailed).Select(line => line.Path));

        _failingDownload = null;
        var applied = await _ApplyAsync();

        Assert.Null(applied.Failure);
        var written = File.ReadAllText(ConfigPath);
        var file = JsonNode.Parse(written)!;
        Assert.Equal(
            ("false", "15", "true"),
            (file["Notifications"]!["LocalEnabled"]!.ToString(), file["Terminal"]!["FontSize"]!.ToString(), file["SessionBehavior"]!["AutoCloseOnExit"]!.ToString()));
        Assert.Equal(["Mine", "Acme dev"], file["Profiles"]!.AsArray().Select(profile => profile!["Label"]!.ToString()));
        Assert.Equal(@"D:\mine", file["Profiles"]![0]!["ConfigDir"]!.ToString());
        Assert.Equal(("acceptEdits", "claude-sonnet-5-5"), (file["Profiles"]![1]!["Defaults"]!["PermissionMode"]!.ToString(), file["Profiles"]![1]!["Provider"]!["Model"]!.ToString()));
        Assert.Equal(
            ["alpha:true:sha-alpha", "beta:true:sha-beta"],
            file["Plugins"]!.AsObject().Select(plugin => $"{plugin.Key}:{plugin.Value!["Enabled"]}:{plugin.Value["PinnedSha256"]}"));
        Assert.Equal(1, file["FirstRunWizardVersion"]!.GetValue<int>());

        // Off the white list: not written, and said.
        Assert.Null(file["Layout"]);
        Assert.Contains(applied.Report, line => line is { Path: "settings.layout.themeMode", Reason: ProvisionManifestReader.SkippedUnknown });

        Assert.Null((await _ApplyAsync()).Failure);
        Assert.Equal(written, File.ReadAllText(ConfigPath));
    }

    private Task<ProvisioningOutcome> _ApplyAsync()
    {
        var read = ProvisionManifestReader.Read(Manifest);
        var storeClient = Substitute.For<IPluginStoreClient>();
        storeClient.FetchIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>())
            .Returns(new PluginStoreFetchResult(true, null, new PluginStoreIndex("test", [_Published("alpha"), _Published("beta")]), "https://store.example/index.json"));
        storeClient.DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = Path.GetFileNameWithoutExtension(call.ArgAt<string>(1));
                if (id == _failingDownload)
                {
                    return new PluginStoreDownloadResult(false, "The store dropped the connection.", null);
                }

                var zip = Path.Combine(_stateRoot, $"{id}.zip");
                File.WriteAllText(zip, id);
                return new PluginStoreDownloadResult(true, null, zip);
            });
        var installer = Substitute.For<IPluginInstaller>();
        installer.InstallFromZipAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = Path.GetFileNameWithoutExtension(call.ArgAt<string>(0));
                Directory.CreateDirectory(Path.Combine(PluginsRoot, id));
                File.WriteAllText(Path.Combine(PluginsRoot, id, "plugin.json"), """{ "version": "1.2.0" }""");
                return PluginInstallResult.Success(id, $"sha-{id}");
            });

        return ProvisionManifestApplier.ApplyAsync(
            new ProvisioningOutcome(read.Manifest, read.Skipped, read.Error),
            _stateRoot,
            PluginsRoot,
            storeClient,
            new PluginProvisioningService(storeClient, installer),
            NullLogger.Instance,
            CancellationToken.None);
    }

    private static PluginStoreEntry _Published(string id) =>
        new(id, id, null, null, "1.2.0", [new PluginStoreVersion("1.2.0", $"{id}.zip", AbstractionsContract.Version, null, null, null)]);

    private const string Manifest = """
        {
          "format": "cockpit.provision", "formatVersion": 1,
          "plugins": { "required": [ { "id": "alpha", "minVersion": "1.0.0", "store": "acme" }, { "id": "beta", "store": "acme" } ] },
          "stores": { "allowed": [ { "id": "acme", "url": "https://store.example/", "auth": "none" } ] },
          "settings": {
            "notifications.localEnabled":     { "value": false, "mode": "default", "by": "org" },
            "terminal.fontSize":              { "value": 15,    "mode": "default", "by": "profile" },
            "sessionBehavior.autoCloseOnExit": { "value": true,  "mode": "locked",  "by": "org" },
            "layout.themeMode":               { "value": "Light", "mode": "default", "by": "profile" }
          },
          "profiles": { "items": [
            { "id": "acme-dev", "label": "Acme dev", "by": "org",
              "provider": { "id": "claude-provider", "model": "claude-sonnet-5-5" },
              "defaults": { "permissionMode": "acceptEdits" } } ] },
          "firstRun": { "workKind": "ontwikkelen" }
        }
        """;
}
