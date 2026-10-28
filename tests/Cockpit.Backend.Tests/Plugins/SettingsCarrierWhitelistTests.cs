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
              "Plugins": { "kubernetes": { "Enabled": true } }
            }
            """;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(blob)), blob);
        _AssertProjectsAreNotExtracted();

        // NeverCarried as an invariant: no carried path may sit on, under or over a machine-bound one.
        Assert.DoesNotContain(
            SettingsCarrierWhitelist.Carried.SelectMany(carried => SettingsCarrierWhitelist.NeverCarried, (carried, never) => (carried, never)),
            pair => _Covers(pair.carried, pair.never) || _Covers(pair.never, pair.carried));
    }

    // AC-1861: project definitions and their category order belong only to project sync.
    private static void _AssertProjectsAreNotExtracted()
    {
        var config = JsonNode.Parse("""
            {"Projects":[{"Id":"p1","Name":"Project","SharedSourceKey":"depot:example/p1"}],
             "CategoryOrder":["Work"],"ProjectsDisplay":{"LayoutMode":"List"}}
            """) as JsonObject ?? throw new InvalidOperationException("The config is not an object.");
        var extracted = JsonNode.Parse(SettingsCarrierWhitelist.Extract(config)) as JsonObject;
        Assert.NotNull(extracted);
        Assert.False(extracted.ContainsKey("Projects"));
        Assert.False(extracted.ContainsKey("CategoryOrder"));
        Assert.Equal("List", extracted["ProjectsDisplay"]?["LayoutMode"]?.GetValue<string>());
    }

    private static bool _Covers(string path, string inner) =>
        inner == path || inner.StartsWith(path + ".", StringComparison.Ordinal) || inner.StartsWith(path + "[]", StringComparison.Ordinal);
}
