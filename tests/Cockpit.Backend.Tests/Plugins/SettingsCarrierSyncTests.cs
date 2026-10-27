using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Settings;

namespace Cockpit.Backend.Tests.Plugins;

// AC-1510: two machines, each a state root of its own, against one carrier that versions its blob as Depot does.
public sealed class SettingsCarrierSyncTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"settings-sync-{Guid.NewGuid():N}")).FullName;
    private readonly _Space _space = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Acceptance 2, the data-loss rule: another machine writes between this one's read and write. The write is refused,
    // the merge runs again and the setting becomes the operator's choice; the other machine's value is never lost.
    [Fact]
    public async Task ASettingBothMachinesChanged_IsAChoice_EvenWhenTheOtherWritesBetweenReadAndWrite()
    {
        var a = _Machine("a", """{"Terminal":{"FontSize":17}}""");
        var b = _Machine("b", """{"Terminal":{"FontSize":17}}""");
        await a.Sync.RunOnceAsync();
        await b.Sync.RunOnceAsync();

        _Write(b, """{"Terminal":{"FontSize":19}}""");
        _space.BeforeWrite = () => _space.Blob = Encoding.UTF8.GetBytes("""{"Terminal":{"FontSize":18}}""");
        await b.Sync.RunOnceAsync();

        Assert.Equal(18, _Remote()["Terminal"]?["FontSize"]?.GetValue<int>());
        Assert.Equal(new SettingsConflict("Terminal.FontSize", "19", "18"), Assert.Single(b.Sync.Conflicts));
        Assert.Equal(19, _Local(b)["Terminal"]?["FontSize"]?.GetValue<int>());

        // Taken over, then changed here again before the next start: still a choice, never a push over the other value.
        await b.Sync.ResolveAsync(new Dictionary<string, bool> { ["Terminal.FontSize"] = false });
        _Write(b, """{"Terminal":{"FontSize":20}}""");
        await b.Sync.RunOnceAsync();
        Assert.Equal((new SettingsConflict("Terminal.FontSize", "20", "18"), 18), (Assert.Single(b.Sync.Conflicts), _Remote()["Terminal"]?["FontSize"]?.GetValue<int>()));

        // Taking the other machine's value lands at the next start; the carrier is left as it is.
        await b.Sync.ResolveAsync(new Dictionary<string, bool> { ["Terminal.FontSize"] = false });
        await SettingsCarrierSync.ApplyIncomingAsync(b.ConfigPath, NullLogger.Instance);
        Assert.Empty(b.Sync.Conflicts);
        Assert.Equal((18, 18), (_Local(b)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote()["Terminal"]?["FontSize"]?.GetValue<int>()));
        await _AssertProjectsWithDifferentIdsAndTheSameGitUrlMergeAsync();
        await _AssertProjectsWithDifferentIdsAndTheSameSharedSourceKeyMergeAsync();
    }

    // Acceptance 4: a key the manifest manages (AC-1507), a leaf or a whole dictionary, is neither pushed nor taken over.
    [Fact]
    public async Task AManagedSetting_IsNeverPushed_NorTakenOver()
    {
        _space.Blob = Encoding.UTF8.GetBytes("""{"Terminal":{"FontSize":20}}""");
        var managed = _Machine("managed", """
            {
              "Terminal": { "FontSize": 15 }, "Layout": { "ThemeMode": "Light" }, "Shortcuts": { "Gestures": { "NewSession": "Ctrl+Alt+N" } },
              "Provisioning": { "Managed": {
                "Terminal.FontSize": { "By": "Acme", "Value": "15" },
                "Shortcuts.Gestures": { "By": "Acme", "Value": "{\"NewSession\":\"Ctrl+Alt+N\"}" } } }
            }
            """);

        await managed.Sync.RunOnceAsync();
        _space.Blob = Encoding.UTF8.GetBytes(_Remote().ToJsonString().Replace("20", "21", StringComparison.Ordinal));
        await managed.Sync.RunOnceAsync();
        await SettingsCarrierSync.ApplyIncomingAsync(managed.ConfigPath, NullLogger.Instance);

        Assert.Equal(("Light", 21), (_Remote()["Layout"]?["ThemeMode"]?.GetValue<string>(), _Remote()["Terminal"]?["FontSize"]?.GetValue<int>()));
        Assert.Equal(15, _Local(managed)["Terminal"]?["FontSize"]?.GetValue<int>());
        Assert.Null(_Remote()["Shortcuts"]?["Gestures"]);
        Assert.Empty(managed.Sync.Conflicts);
    }

    // AC-1834: repository identity joins machine-local project ids without adding a no-folder duplicate.
    private async Task _AssertProjectsWithDifferentIdsAndTheSameGitUrlMergeAsync()
    {
        var a = _Machine("merge-desktop", """{"Projects":[{"Id":"desktop","Name":"Cockpit","GitUrl":"https://github.com/krahwinkel-it/cockpit.git","SourceDirectory":"C:/src/Cockpit"}]}""");
        var b = _Machine("merge-hub", """{"Projects":[{"Id":"hub","Name":"Cockpit","GitUrl":"git@github.com:krahwinkel-it/cockpit.git","SourceDirectory":"/srv/Cockpit"}]}""");

        await a.Sync.RunOnceAsync();
        await b.Sync.RunOnceAsync();
        await SettingsCarrierSync.ApplyIncomingAsync(b.ConfigPath, NullLogger.Instance);

        var project = Assert.Single(_Local(b)["Projects"]!.AsArray());
        Assert.Equal("hub", project?["Id"]?.GetValue<string>());
        Assert.Equal("/srv/Cockpit", project?["SourceDirectory"]?.GetValue<string>());
    }

    // AC-1837: shared-source identity joins machine-local project ids when no repository identity is available.
    private async Task _AssertProjectsWithDifferentIdsAndTheSameSharedSourceKeyMergeAsync()
    {
        var space = new _Space();
        var a = _Machine("shared-source-desktop", """{"Projects":[{"Id":"desktop","Name":"Cockpit","SharedSourceKey":"depot:depot.example.com/krahwinkel-it/cockpit","SourceDirectory":"C:/src/Cockpit"}]}""", space);
        var b = _Machine("shared-source-hub", """{"Projects":[{"Id":"hub","Name":"Cockpit","SharedSourceKey":"depot:depot.example.com/krahwinkel-it/cockpit","SourceDirectory":"/srv/Cockpit"}]}""", space);

        await a.Sync.RunOnceAsync();
        await b.Sync.RunOnceAsync();
        await SettingsCarrierSync.ApplyIncomingAsync(b.ConfigPath, NullLogger.Instance);

        var project = Assert.Single(_Local(b)["Projects"]!.AsArray());
        Assert.Equal("hub", project?["Id"]?.GetValue<string>());
        Assert.Equal("/srv/Cockpit", project?["SourceDirectory"]?.GetValue<string>());
    }

    private (string ConfigPath, SettingsCarrierSync Sync) _Machine(string name, string config, _Space? space = null)
    {
        var path = CockpitConfigPath.For(Directory.CreateDirectory(Path.Combine(_root, name)).FullName);
        File.WriteAllText(path, config);
        var carriers = new SettingsCarriers(NullLogger<SettingsCarriers>.Instance);
        carriers.Add("fake", (space ?? _space).Carrier);
        return (path, new SettingsCarrierSync(carriers, path, null, null, TimeProvider.System));
    }

    private static void _Write((string ConfigPath, SettingsCarrierSync Sync) machine, string config) => File.WriteAllText(machine.ConfigPath, config);

    private static JsonNode _Local((string ConfigPath, SettingsCarrierSync Sync) machine) =>
        JsonNode.Parse(File.ReadAllText(machine.ConfigPath)) ?? throw new InvalidOperationException("cockpit.json is empty.");

    private JsonNode _Remote() => JsonNode.Parse(_space.Blob ?? []) ?? throw new InvalidOperationException("The carrier holds nothing.");

    // Depot's /cockpit/profile in memory: a write names the version it replaces and is refused when that is stale.
    private sealed class _Space
    {
        public byte[]? Blob { get; set; }

        // Another machine's write, landing once just before this one's.
        public Action? BeforeWrite { get; set; }

        public SettingsCarrierRegistration Carrier => new(
            "fake",
            "Fake",
            "fake://account",
            _ => Task.FromResult(Blob is not null),
            _ => Task.FromResult(Blob),
            (blob, _) =>
            {
                Blob = blob;
                return Task.CompletedTask;
            })
        {
            WriteIfUnchangedAsync = (blob, version, _) =>
            {
                var before = BeforeWrite;
                BeforeWrite = null;
                before?.Invoke();
                if ((Blob is null ? null : SettingsCarrierSync.Version(Blob)) != version)
                {
                    return Task.FromResult(false);
                }

                Blob = blob;
                return Task.FromResult(true);
            },
        };
    }
}
