using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Core.Provisioning;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Provisioning;
using Cockpit.Infrastructure.Terminal;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Infrastructure.Tests.Provisioning;

// AC-1506 (DB-2.5b): the applier's first phase, the one that overwrites cockpit.json. All or nothing up to that write,
// and "set", not "add": applying the same manifest again leaves the file byte for byte as it was.
public sealed class ProvisionManifestApplierTests : IDisposable
{
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"cockpit-apply-{Guid.NewGuid():N}");

    private string? _failingDownload;

    private readonly List<string?> _fetchedWithToken = [];

    public ProvisionManifestApplierTests() => Directory.CreateDirectory(_stateRoot);

    public void Dispose() => Directory.Delete(_stateRoot, recursive: true);

    private string ConfigPath => Path.Combine(_stateRoot, "cockpit.json");

    private string PluginsRoot => Path.Combine(_stateRoot, "plugins");

    [Fact]
    public async Task A_failed_plugin_download_writes_nothing_and_names_the_plugin_then_the_manifest_applies_and_a_second_time_changes_nothing()
    {
        File.WriteAllText(ConfigPath, """
            { "Profiles": [ { "Label": "Mine", "ConfigDir": "D:\\mine" } ], "Terminal": { "FontSize": 11 },
              "PluginStores": [ { "kind": "remote", "location": "https://store.example/", "token": "entered-here" } ] }
            """);
        var before = File.ReadAllBytes(ConfigPath);

        _failingDownload = "beta";
        var failed = await _ApplyAsync();

        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Contains("plugins.beta", failed.Failure);
        Assert.Equal(["plugins.beta"], failed.Report.Where(line => line.Reason == ProvisionManifestApplier.PluginFailed).Select(line => line.Path));

        // A private store's token is the one entered on this machine; a private store without one is named, not left to fail.
        Assert.Equal(["entered-here"], _fetchedWithToken.Distinct());
        Assert.Contains("stores.acme-intern (enter the token for store acme-intern (https://plugins.acme.nl/) under Manage stores)", failed.Failure);
        Assert.Equal(["stores.acme-intern"], failed.Report.Where(line => line.Reason == ProvisionManifestApplier.TokenNeeded).Select(line => line.Path));

        _failingDownload = null;
        var applied = await _ApplyAsync();

        Assert.Null(applied.Failure);
        Assert.Equal(["stores.acme-intern"], applied.Report.Where(line => line.Reason == ProvisionManifestApplier.TokenNeeded).Select(line => line.Path));
        var written = File.ReadAllText(ConfigPath);
        var file = JsonNode.Parse(written)!;
        // AC-1507: the font size of 11 was chosen here before, so the manifest's default gives way to it.
        Assert.Equal(
            ("false", "11", "true"),
            (file["Notifications"]!["LocalEnabled"]!.ToString(), file["Terminal"]!["FontSize"]!.ToString(), file["SessionBehavior"]!["AutoCloseOnExit"]!.ToString()));
        Assert.Contains(applied.Report, line => line is { Path: "Terminal.FontSize", Reason: ProvisionManifestApplier.KeptLocal });
        Assert.Equal(["Mine", "Acme dev"], file["Profiles"]!.AsArray().Select(profile => profile!["Label"]!.ToString()));
        Assert.Equal(@"D:\mine", file["Profiles"]![0]!["ConfigDir"]!.ToString());
        Assert.Equal(("acceptEdits", "claude-sonnet-5-5"), (file["Profiles"]![1]!["Defaults"]!["PermissionMode"]!.ToString(), file["Profiles"]![1]!["Provider"]!["Model"]!.ToString()));
        Assert.Equal(
            ["alpha:true:sha-alpha", "beta:true:sha-beta", "gamma:true:sha-gamma"],
            file["Plugins"]!.AsObject().Select(plugin => $"{plugin.Key}:{plugin.Value!["Enabled"]}:{plugin.Value["PinnedSha256"]}"));
        Assert.Equal(1, file["FirstRunWizardVersion"]!.GetValue<int>());

        // AC-1512, §3.10: the first-run answers landed as defaults, not just as "the wizard was answered": Simple on open,
        // the work kind's plugin (gamma, tagged in the store), the manifest profile's reading level. Nothing to say.
        Assert.Equal(("true", "Simple"), (file["Layout"]?["OpenInSimpleView"]?.ToString(), file["Profiles"]?[1]?["Defaults"]?["DefaultReadingLevel"]?.ToString()));
        Assert.DoesNotContain(applied.Report, line => line.Path.StartsWith("firstRun", StringComparison.Ordinal));

        // Off the white list: not written, and said.
        Assert.NotEqual("Light", file["Layout"]?["ThemeMode"]?.ToString());
        Assert.Contains(applied.Report, line => line is { Path: "settings.layout.themeMode", Reason: ProvisionManifestReader.SkippedUnknown });

        var again = await _ApplyAsync();
        Assert.Null(again.Failure);
        Assert.Equal(written, File.ReadAllText(ConfigPath));
        string[] changes = [ProvisionManifestApplier.Applied, ProvisionManifestApplier.KeptLocal, ProvisionManifestApplier.ReplacedManaged, ProvisionManifestApplier.RestoredUnmanaged];
        Assert.DoesNotContain(again.Report, line => changes.Contains(line.Reason));

        // What a first-run answer cannot do is said, never passed over.
        var refused = await _ApplyAsync(_FirstRun("""{ "workKind": "writing", "readingLevel": "Expert" }"""));
        Assert.Equal(["firstRun.workKind", "firstRun.readingLevel"], refused.Report.Where(line => line.Reason == ProvisionManifestApplier.NotApplied).Select(line => line.Path));
    }

    // AC-1507, manifest-v1 §4.2: the rows where a local value could be lost. Each applies `first`, lets the operator
    // set 11 through the Options route, then applies `second` (null: the key left the manifest).
    [Theory]
    [InlineData("default", "default", 16, "11", ProvisionManifestApplier.KeptLocal)]
    [InlineData("locked", null, 0, "11", ProvisionManifestApplier.RestoredUnmanaged)]
    public async Task A_local_choice_outlives_a_new_default_and_comes_back_when_a_lock_is_lifted(string first, string? second, int secondValue, string expected, string outcome)
    {
        var store = new TerminalSettingsStore(ConfigPath);
        if (first == ProvisionManifestReader.LockedMode)
        {
            await store.SaveAsync(new TerminalSettings { FontSize = 11 });
        }

        Assert.Null((await _ApplyAsync(_FontSize(first, 15))).Failure);
        Assert.Equal(15, (await store.LoadAsync()).FontSize);
        if (first == ProvisionManifestReader.DefaultMode)
        {
            await store.SaveAsync(new TerminalSettings { FontSize = 11 });
        }

        var applied = await _ApplyAsync(second is null ? _FontSize(null, 0) : _FontSize(second, secondValue));

        Assert.Equal(expected, (await store.LoadAsync()).FontSize.ToString(CultureInfo.InvariantCulture));
        Assert.Contains(applied.Report, line => line.Path == "Terminal.FontSize" && line.Reason == outcome);
    }

    // AC-1507: a locked key is refused on the route every Options save takes, and the Options lookup names who locked it.
    [Fact]
    public async Task A_write_to_a_locked_key_is_refused_and_the_key_reads_as_managed_by_its_organisation()
    {
        Assert.Null((await _ApplyAsync(_FontSize(ProvisionManifestReader.LockedMode, 15))).Failure);

        var store = new TerminalSettingsStore(ConfigPath);
        await store.SaveAsync(new TerminalSettings { FontSize = 20, FontFamily = "Consolas" });

        Assert.Equal((15, "Consolas"), ((await store.LoadAsync()).FontSize, (await store.LoadAsync()).FontFamily));
        Assert.Equal("Acme", (await new ManagedSettings(ConfigPath).ReadAsync())["Terminal.FontSize"]);
    }

    private static string _FirstRun(string firstRun) => $$"""
        {
          "format": "cockpit.provision", "formatVersion": 1,
          "issuer": { "kind": "depot", "organization": { "id": "acme", "name": "Acme" } },
          "stores": { "allowed": [ { "id": "acme", "url": "https://store.example/", "auth": "none" } ] },
          "profiles": { "items": [ { "id": "acme-dev", "label": "Acme dev", "by": "org", "provider": { "id": "claude-provider" } } ] },
          "firstRun": {{firstRun}}
        }
        """;

    // AC-1567, manifest-v1 §3.6: the rule set lands in its own file and nowhere else. The personal files stay byte for
    // byte through write, update and removal; the memory tools cannot reach the org file, and an export leaves it out.
    [Fact]
    public async Task The_org_rule_set_is_its_own_file_that_the_personal_memory_never_reaches_and_that_leaves_with_the_manifest()
    {
        var memory = new AssistantMemoryFile(Path.Combine(_stateRoot, "assistant-memory.md"), Path.Combine(_stateRoot, "assistant-state.md"));
        await memory.RememberAsync("The operator is called Raymond.", AssistantMemoryScope.Behaviour);
        await memory.RememberAsync("Docker runs in WSL.", AssistantMemoryScope.Machine);
        await memory.NoteCurrentStateAsync("On AC-1567.");
        string[] personal = ["assistant-memory.md", "assistant-machine.md", "assistant-state.md"];
        List<byte[]> Personal() => [.. personal.Select(name => File.ReadAllBytes(Path.Combine(_stateRoot, name)))];
        var before = Personal();
        var orgFile = Path.Combine(_stateRoot, "assistant-org-rules.md");

        var written = await _ApplyAsync(_OrgRules("Answer customers in Dutch."));
        Assert.Contains(written.Report, line => line is { Path: "assistant.orgRules", Detail: "rule set of Acme written" });
        Assert.StartsWith("# Rule set of Acme\n\nSet by Acme.", File.ReadAllText(orgFile), StringComparison.Ordinal);
        Assert.Contains("Answer customers in Dutch.", await memory.ReadOrgRulesAsync(), StringComparison.Ordinal);
        Assert.Equal(before, Personal());

        await memory.RememberAsync("Prefers short answers.", AssistantMemoryScope.Behaviour);
        await memory.RememberAsync("Has a GPU.", AssistantMemoryScope.Machine);
        Assert.DoesNotContain("Prefers short answers.", File.ReadAllText(orgFile), StringComparison.Ordinal);
        Assert.DoesNotContain("Has a GPU.", File.ReadAllText(orgFile), StringComparison.Ordinal);
        Assert.DoesNotContain("assistant-org-rules.md", await memory.ExportAsync(Path.Combine(_stateRoot, "export.zip")));
        before = Personal();

        var updated = await _ApplyAsync(_OrgRules("Answer customers in English."));
        Assert.Contains(updated.Report, line => line is { Path: "assistant.orgRules", Detail: "rule set of Acme updated" });
        Assert.Contains("Answer customers in English.", File.ReadAllText(orgFile), StringComparison.Ordinal);

        var removed = await _ApplyAsync(_FontSize(null, 0));
        Assert.Contains(removed.Report, line => line is { Path: "assistant.orgRules", Detail: "the organisation's rule set was removed" });
        Assert.False(File.Exists(orgFile));
        Assert.Equal(before, Personal());
    }

    private static string _OrgRules(string content) => $$"""
        {
          "format": "cockpit.provision", "formatVersion": 1,
          "issuer": { "kind": "depot", "organization": { "id": "acme", "name": "Acme" } },
          "assistant": { "orgRules": { "by": "org", "title": "Assistentregels Acme", "content": "{{content}}" } }
        }
        """;

    private static string _FontSize(string? mode, int value)
    {
        var settings = mode is null ? "" : $$""", "settings": { "terminal.fontSize": { "value": {{value}}, "mode": "{{mode}}", "by": "org" } }""";
        return $$"""
            {
              "format": "cockpit.provision", "formatVersion": 1,
              "issuer": { "kind": "depot", "organization": { "id": "acme", "name": "Acme" } }{{settings}}
            }
            """;
    }

    private Task<ProvisioningOutcome> _ApplyAsync(string manifest = Manifest)
    {
        var read = ProvisionManifestReader.Read(manifest);
        var storeClient = Substitute.For<IPluginStoreClient>();
        storeClient.FetchIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _fetchedWithToken.Add(call.Arg<PluginStoreConfig>().Token);
                return new PluginStoreFetchResult(true, null, new PluginStoreIndex("test", [_Published("alpha"), _Published("beta"), _Published("gamma")]), "https://store.example/index.json");
            });
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
        new(id, id, null, null, "1.2.0", [new PluginStoreVersion("1.2.0", $"{id}.zip", AbstractionsContract.Version, null, null, null)],
            Audience: id == "gamma" ? [PluginWorkKinds.DocumentsAndDesign] : null);

    private const string Manifest = """
        {
          "format": "cockpit.provision", "formatVersion": 1,
          "plugins": { "required": [ { "id": "alpha", "minVersion": "1.0.0", "store": "acme" }, { "id": "beta", "store": "acme" } ] },
          "stores": { "allowed": [ { "id": "acme", "url": "https://store.example/", "auth": "token-required" } ],
                      "added": [ { "id": "acme-intern", "url": "https://plugins.acme.nl/", "auth": "token-required" } ] },
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
          "firstRun": { "workKind": "documents-and-design", "readingLevel": "Simple", "simpleView": true }
        }
        """;
}
