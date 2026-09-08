using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Backup;

namespace Cockpit.Core.Tests.Backup;

public class RestorePathPortabilityEmbeddedTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Rebase_WhenPluginBlobContainsAConfigPath_ReanchorsIt(int depth)
    {
        const string sourceRoot = @"C:\source";
        const string targetRoot = @"D:\target";
        var blob = JsonSerializer.Serialize(new Dictionary<string, string> { ["Path"] = $"{sourceRoot}\\cache" });

        for (var level = 1; level < depth; level++)
        {
            blob = JsonSerializer.Serialize(new Dictionary<string, string> { ["Nested"] = blob });
        }

        var settings = PluginStoring(blob);

        RestorePathPortability.Rebase(settings, sourceRoot, targetRoot);

        var rebased = JsonNode.Parse(settings["Plugins"]!["a-plugin"]!["Data"]!["cache"]!.GetValue<string>())!;
        for (var level = 1; level < depth; level++)
        {
            rebased = JsonNode.Parse(rebased["Nested"]!.GetValue<string>())!;
        }

        Assert.Equal(Path.Combine(targetRoot, "cache"), rebased["Path"]!.GetValue<string>());
    }

    [Fact]
    public void Rebase_WhenPluginBlobHasNoConfigPath_PreservesItsBytes()
    {
        const string blob = "{ \"z\": 3, \"a\": [1, 2] }";
        var settings = PluginStoring(blob);

        RestorePathPortability.Rebase(settings, @"C:\source", @"D:\target");

        Assert.Equal(blob, settings["Plugins"]!["a-plugin"]!["Data"]!["cache"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ordinary text")]
    [InlineData("42")]
    [InlineData("{ unfinished")]
    public void Rebase_WhenPluginDataIsNotJson_LeavesItUntouched(string blob)
    {
        var settings = PluginStoring(blob);

        RestorePathPortability.Rebase(settings, @"C:\source", @"D:\target");

        Assert.Equal(blob, settings["Plugins"]!["a-plugin"]!["Data"]!["cache"]!.GetValue<string>());
    }

    private static JsonObject PluginStoring(string data) => new()
    {
        ["Plugins"] = new JsonObject
        {
            ["a-plugin"] = new JsonObject { ["Data"] = new JsonObject { ["cache"] = data } },
        },
    };
}
