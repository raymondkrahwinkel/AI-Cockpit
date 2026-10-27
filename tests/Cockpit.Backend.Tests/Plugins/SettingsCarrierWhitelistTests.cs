using System.Text;
using System.Text.Json.Nodes;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Backend.Tests.Plugins;

public class SettingsCarrierWhitelistTests
{
    // AC-540 acceptance 5: a field nobody listed stays here, wherever it appears, and so does every machine-bound path.
    [Fact]
    public void Extract_CarriesOnlyListedFields_LeavingUnknownAndMachineBoundOnesBehind()
    {
        var config = JsonNode.Parse("""
            {
              "SomeNewSection": { "Token": "s3cret" },
              "Profiles": [ {
                "Label": "Work", "SomeNewField": "s3cret",
                "Provider": { "Model": "opus", "ApiKey": "s3cret", "PluginConfigJson": "{\"Command\":\"C:\\\\codex.exe\"}" },
                "Delegation": { "MaxConcurrent": 2, "AllowedWorkingDirs": [ "C:\\src" ] }
              } ],
              "Projects": [ {
                "Id": "p1", "SourceDirectory": "C:\\src\\p1", "SourceDirectories": [ { "Path": "C:\\src\\p1" } ],
                "Resources": [ { "Reference": "depot://x" } ], "MemoryRef": "depot://m"
              } ],
              "Plugins": { "kubernetes": { "Enabled": true, "SomeNewFlag": true, "Data": { "KubeconfigPath": "C:\\kube" } } },
              "WorkingPaths": { "Recent": [ "C:\\src" ] }
            }
            """) as JsonObject ?? throw new InvalidOperationException("The config is not an object.");

        var blob = Encoding.UTF8.GetString(SettingsCarrierWhitelist.Extract(config));

        var expected = """
            {
              "Profiles": [ { "Label": "Work", "Provider": { "Model": "opus" }, "Delegation": { "MaxConcurrent": 2 } } ],
              "Projects": [ { "Id": "p1" } ],
              "Plugins": { "kubernetes": { "Enabled": true } }
            }
            """;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(blob)), blob);

        // NeverCarried as an invariant: no carried path may sit on, under or over a machine-bound one.
        Assert.DoesNotContain(
            SettingsCarrierWhitelist.Carried.SelectMany(carried => SettingsCarrierWhitelist.NeverCarried, (carried, never) => (carried, never)),
            pair => _Covers(pair.carried, pair.never) || _Covers(pair.never, pair.carried));
    }

    [Fact]
    public void Extract_ResolvesCarrierCategoriesAndProfiles()
    {
        JsonObject Config(string sync) => JsonNode.Parse("{\"Profiles\":[{\"Label\":\"Work\"},{\"Label\":\"Home\"}],\"Plugins\":{\"demo\":{\"Enabled\":true}},\"Notifications\":{\"LocalEnabled\":true},\"Updates\":{\"CheckOnStartup\":true},\"SettingsSync\":{" + sync + "}}")!.AsObject();
        JsonNode? Extract(JsonObject config, string id) => JsonNode.Parse(Encoding.UTF8.GetString(SettingsCarrierWhitelist.Extract(config, SettingsCarrierFilter.FromConfig(config, id))));

        var config = Config("\"Categories\":{\"Profiles\":[\"one\",\"two\"],\"Plugins\":[\"one\"],\"Notifications\":[\"two\"]},\"Profiles\":{\"Work\":[\"one\"],\"Home\":[\"two\"]}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"Profiles":[{"Label":"Work"}],"Plugins":{"demo":{"Enabled":true}}}"""), Extract(config, "one")));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"Profiles":[{"Label":"Home"}],"Notifications":{"LocalEnabled":true}}"""), Extract(config, "two")));

        config = Config("\"Categories\":{\"Profiles\":[\"one\"],\"Updates\":[]}");
        Assert.Equal(2, Extract(config, "one")?["Profiles"]?.AsArray().Count);
        Assert.Null(Extract(config, "one")?["Updates"]);

        config = Config("\"Categories\":{\"Plugins\":[\"one\",\"two\"]}");
        Assert.True(JsonNode.DeepEquals(Extract(config, "one"), Extract(config, "two")));
        Assert.NotNull(Extract(config, "one")?["Plugins"]);
    }

    private static bool _Covers(string path, string inner) =>
        inner == path || inner.StartsWith(path + ".", StringComparison.Ordinal) || inner.StartsWith(path + "[]", StringComparison.Ordinal);
}
