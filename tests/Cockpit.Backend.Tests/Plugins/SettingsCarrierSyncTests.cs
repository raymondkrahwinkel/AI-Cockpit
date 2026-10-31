using System.Text;
using System.Text.Json;
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
        await _AssertProjectsStayLocalAsync("forward-a", "forward-b");
        await _AssertProjectsStayLocalAsync("reverse-b", "reverse-a");
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

    [Fact]
    public async Task EligibleCarriers_PropagateChanges_AndLegacyStateMigratesIdempotently()
    {
        var one = new _Space("one");
        var two = new _Space("two");
        var machine = _Machine("two-carriers", """
            { "Terminal": { "FontSize": 12 }, "SettingsSync": { "Categories": { "Appearance": [ "one", "two" ] } } }
            """, one, two);

        await machine.Sync.RunOnceAsync();
        Assert.Equal((12, 12), (_Remote(one)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote(two)["Terminal"]?["FontSize"]?.GetValue<int>()));

        one.Blob = Encoding.UTF8.GetBytes("""{"Terminal":{"FontSize":13}}""");
        await machine.Sync.RunOnceAsync();
        await SettingsCarrierSync.ApplyIncomingAsync(machine.ConfigPath, NullLogger.Instance);
        await machine.Sync.RunOnceAsync();
        Assert.Equal((13, 13, 13), (_Local(machine)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote(one)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote(two)["Terminal"]?["FontSize"]?.GetValue<int>()));

        one.Blob = Encoding.UTF8.GetBytes("""{"Terminal":{"FontSize":14}}""");
        two.Blob = Encoding.UTF8.GetBytes("""{"Terminal":{"FontSize":15}}""");
        await machine.Sync.RunOnceAsync();

        Assert.Equal(13, _Local(machine)["Terminal"]?["FontSize"]?.GetValue<int>());
        Assert.Equal((14, 15), (_Remote(one)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote(two)["Terminal"]?["FontSize"]?.GetValue<int>()));
        Assert.Single(machine.Sync.Conflicts);

        await machine.Sync.ResolveAsync(new Dictionary<string, bool> { ["Terminal.FontSize"] = true });
        Assert.Equal((13, 13), (_Remote(one)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote(two)["Terminal"]?["FontSize"]?.GetValue<int>()));

        one.Blob = Encoding.UTF8.GetBytes("""{"Terminal":{}}""");
        await machine.Sync.RunOnceAsync();
        Assert.Empty(machine.Sync.Conflicts);
        Assert.Equal((null, 13), (_Remote(one)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote(two)["Terminal"]?["FontSize"]?.GetValue<int>()));
        await machine.Sync.RunOnceAsync();
        Assert.Equal((13, 13), (_Remote(one)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote(two)["Terminal"]?["FontSize"]?.GetValue<int>()));

        var populatedOne = new _Space("one") { Blob = Encoding.UTF8.GetBytes("""{"Terminal":{"FontSize":20}}""") };
        var populated = _Machine("populated", """
            { "Terminal": { "FontSize": 12 }, "SettingsSync": { "Categories": { "Appearance": [ "one" ] } } }
            """, populatedOne);
        await populated.Sync.RunOnceAsync();
        var firstContact = Assert.Single(populated.Sync.Conflicts);
        Assert.Equal((12, 20, "20"), (_Local(populated)["Terminal"]?["FontSize"]?.GetValue<int>(), _Remote(populatedOne)["Terminal"]?["FontSize"]?.GetValue<int>(), firstContact.CarrierValues?["one"]));

        var untickedOne = new _Space("one");
        var untickedTwo = new _Space("two");
        var unticked = _Machine("unticked", """
            { "Terminal": { "FontSize": 12 }, "SettingsSync": { "Categories": { "Appearance": [ "one", "two" ] } } }
            """, untickedOne, untickedTwo);

        await unticked.Sync.RunOnceAsync();
        _Write(unticked, """
            { "Terminal": { "FontSize": 13 }, "SettingsSync": { "Categories": { "Appearance": [ "one" ] } } }
            """);
        await unticked.Sync.RunOnceAsync();

        Assert.Equal(13, _Remote(untickedOne)["Terminal"]?["FontSize"]?.GetValue<int>());
        Assert.Equal(12, _Remote(untickedTwo)["Terminal"]?["FontSize"]?.GetValue<int>());

        await _AssertLegacySingleCarrierStateMigratesWithoutWritingOrConflictsAsync();
    }

    private async Task _AssertLegacySingleCarrierStateMigratesWithoutWritingOrConflictsAsync()
    {
        var machine = _Machine("legacy", """
            { "Profiles": [{ "Label": "work", "Purpose": "work" }], "Terminal": { "FontSize": 19, "FontFamily": "Cascadia Mono, Consolas, monospace" } }
            """);
        _space.Blob = Encoding.UTF8.GetBytes(_Local(machine).ToJsonString());
        var statePath = Path.Combine(Assert.IsType<string>(Path.GetDirectoryName(machine.ConfigPath)), "settings-sync.json");
        File.WriteAllText(statePath, """
            { "Base": { "Profiles.work": "{\"Label\":\"work\",\"Purpose\":\"work\"}", "Terminal.FontSize": "19", "Terminal.FontFamily": "\"Cascadia Mono, Consolas, monospace\"" } }
            """);

        await machine.Sync.RunOnceAsync();

        var migrated = _Local(machine);
        var settingsSync = Assert.IsType<JsonObject>(migrated["SettingsSync"]);
        var categories = Assert.IsType<JsonObject>(settingsSync["Categories"]);
        Assert.Equal(8, categories.Count);
        Assert.All(categories, category => Assert.Equal("fake", Assert.IsAssignableFrom<JsonValue>(Assert.Single(Assert.IsType<JsonArray>(category.Value))).GetValue<string>()));
        Assert.Null(settingsSync["Profiles"]);
        Assert.Equal(0, _space.WriteCount);
        Assert.Empty(machine.Sync.Conflicts);

        var configAfterFirstStart = File.ReadAllText(machine.ConfigPath);
        var stateAfterFirstStart = File.ReadAllText(statePath);

        await machine.Sync.RunOnceAsync();

        Assert.Equal(configAfterFirstStart, File.ReadAllText(machine.ConfigPath));
        Assert.Equal(stateAfterFirstStart, File.ReadAllText(statePath));
        Assert.Equal(0, _space.WriteCount);
        Assert.Empty(machine.Sync.Conflicts);

        migrated["Profiles"]!.AsArray().Add(new JsonObject { ["Label"] = "later", ["Purpose"] = "later" });
        _Write(machine, migrated.ToJsonString());
        await machine.Sync.RunOnceAsync();

        Assert.Equal("later", Assert.IsType<JsonObject>(Assert.Single(_Remote()["Profiles"]!.AsArray(), profile => profile?["Label"]?.GetValue<string>() == "later"))["Purpose"]?.GetValue<string>());
    }

    // AC-1861: each direction uses separate state roots and exercises an older carrier and pending state.
    private async Task _AssertProjectsStayLocalAsync(string ownerName, string receiverName)
    {
        var space = new _Space();
        var ownerConfig = """{"Projects":[{"Id":"owner","Name":"Owner","SourceDirectory":"C:/owner"}],"CategoryOrder":["Owner"],"Terminal":{"FontSize":17}}""";
        var receiverConfig = """{"Projects":[{"Id":"copy","Name":"Existing copy","SourceDirectory":"C:/copy"}],"CategoryOrder":["Local"],"Terminal":{"FontSize":17}}""";
        ownerConfig = JsonSerializer.Serialize(JsonSerializer.Deserialize<CockpitConfigFile>(ownerConfig, CockpitConfigFileAccess.SerializerOptions), CockpitConfigFileAccess.SerializerOptions);
        receiverConfig = JsonSerializer.Serialize(JsonSerializer.Deserialize<CockpitConfigFile>(receiverConfig, CockpitConfigFileAccess.SerializerOptions), CockpitConfigFileAccess.SerializerOptions);
        var owner = _Machine(ownerName, ownerConfig, space);
        var receiver = _Machine(receiverName, receiverConfig, space);
        await owner.Sync.RunOnceAsync();
        var outgoing = JsonNode.Parse(space.Blob ?? []) as JsonObject;
        Assert.NotNull(outgoing);
        Assert.False(outgoing.ContainsKey("Projects"));
        Assert.False(outgoing.ContainsKey("CategoryOrder"));

        // An older client still sends projects; even a pending deletion from the old sync must be ignored.
        space.Blob = Encoding.UTF8.GetBytes(ownerConfig);
        var statePath = Path.Combine(Path.GetDirectoryName(receiver.ConfigPath) ?? ".", "settings-sync.json");
        File.WriteAllText(statePath, """
            {"ProjectIds":{"owner":"copy"},"Base":{"Projects.owner":"{}","CategoryOrder":"[]"},
             "Incoming":{"Projects.copy":{"Value":null,"Was":null,"Steps":[{"Name":"Projects","Element":"copy"}]},
                         "CategoryOrder":{"Value":"[]","Was":null,"Steps":[{"Name":"CategoryOrder","Element":null}]}},
             "Conflicts":{"Projects.owner":{"Mine":"{}","Theirs":null,"Steps":[{"Name":"Projects","Element":"owner"}]}}}
            """);
        await SettingsCarrierSync.ApplyIncomingAsync(receiver.ConfigPath, NullLogger.Instance);
        await receiver.Sync.RunOnceAsync();
        await SettingsCarrierSync.ApplyIncomingAsync(receiver.ConfigPath, NullLogger.Instance);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(receiverConfig)?["Projects"], _Local(receiver)["Projects"]));
        Assert.Equal("Local", _Local(receiver)["CategoryOrder"]?[0]?.GetValue<string>());
        Assert.Empty(receiver.Sync.Conflicts);
        var saved = JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject;
        Assert.NotNull(saved);
        Assert.False(saved.ContainsKey("ProjectIds"));
        outgoing = JsonNode.Parse(space.Blob ?? []) as JsonObject;
        Assert.NotNull(outgoing);
        Assert.False(outgoing.ContainsKey("Projects"));
        Assert.False(outgoing.ContainsKey("CategoryOrder"));

        // Removing a local duplicate cannot remove the owner's project.
        _Write(receiver, """{"Projects":[],"Terminal":{"FontSize":18}}""");
        await receiver.Sync.RunOnceAsync();
        outgoing = JsonNode.Parse(space.Blob ?? []) as JsonObject;
        Assert.NotNull(outgoing);
        Assert.False(outgoing.ContainsKey("Projects"));
        Assert.False(outgoing.ContainsKey("CategoryOrder"));
        await owner.Sync.RunOnceAsync();
        await SettingsCarrierSync.ApplyIncomingAsync(owner.ConfigPath, NullLogger.Instance);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ownerConfig)?["Projects"], _Local(owner)["Projects"]));
        Assert.Equal("Owner", _Local(owner)["CategoryOrder"]?[0]?.GetValue<string>());
        Assert.Empty(owner.Sync.Conflicts);
        owner.Sync.Dispose();
        receiver.Sync.Dispose();
    }

    private (string ConfigPath, SettingsCarrierSync Sync) _Machine(string name, string config)
    {
        var path = CockpitConfigPath.For(Directory.CreateDirectory(Path.Combine(_root, name)).FullName);
        File.WriteAllText(path, config);
        var carriers = new SettingsCarriers(NullLogger<SettingsCarriers>.Instance);
        carriers.Add("fake", _space.Carrier);
        return (path, new SettingsCarrierSync(carriers, path, null, null, TimeProvider.System));
    }

    private (string ConfigPath, SettingsCarrierSync Sync) _Machine(string name, string config, params _Space[] spaces)
    {
        var path = CockpitConfigPath.For(Directory.CreateDirectory(Path.Combine(_root, name)).FullName);
        File.WriteAllText(path, config);
        var carriers = new SettingsCarriers(NullLogger<SettingsCarriers>.Instance);
        foreach (var space in spaces)
        {
            carriers.Add("fake", space.Carrier);
        }
        return (path, new SettingsCarrierSync(carriers, path, null, null, TimeProvider.System));
    }

    private static void _Write((string ConfigPath, SettingsCarrierSync Sync) machine, string config) => File.WriteAllText(machine.ConfigPath, config);

    private static JsonNode _Local((string ConfigPath, SettingsCarrierSync Sync) machine) =>
        JsonNode.Parse(File.ReadAllText(machine.ConfigPath)) ?? throw new InvalidOperationException("cockpit.json is empty.");

    private JsonNode _Remote() => JsonNode.Parse(_space.Blob ?? []) ?? throw new InvalidOperationException("The carrier holds nothing.");

    private static JsonNode _Remote(_Space space) => JsonNode.Parse(space.Blob ?? []) ?? throw new InvalidOperationException("The carrier holds nothing.");

    // Depot's /cockpit/profile in memory: a write names the version it replaces and is refused when that is stale.
    private sealed class _Space
    {
        public _Space(string id = "fake") => Id = id;

        private string Id { get; }

        public byte[]? Blob { get; set; }

        public int WriteCount { get; private set; }

        // Another machine's write, landing once just before this one's.
        public Action? BeforeWrite { get; set; }

        public SettingsCarrierRegistration Carrier => new(
            Id,
            Id,
            "fake://account",
            _ => Task.FromResult(Blob is not null),
            _ => Task.FromResult(Blob),
            (blob, _) =>
            {
                WriteCount++;
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

                WriteCount++;
                Blob = blob;
                return Task.FromResult(true);
            },
        };
    }
}
