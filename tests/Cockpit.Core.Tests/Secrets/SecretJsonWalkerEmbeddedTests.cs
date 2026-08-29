using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Secrets;

namespace Cockpit.Core.Tests.Secrets;

/// <summary>
/// A plugin's storage is JSON inside a string, and since AC-1152 the walker only builds that string into a tree
/// when a scan of its bytes says there is something in it to rewrite — parsing every plugin's cache on every read
/// and every write was over half of what one config round trip allocated. These are the cases the scan must not
/// miss, because a credential it skips is a credential that stays in the clear with nothing going red.
/// </summary>
public class SecretJsonWalkerEmbeddedTests
{
    [Fact]
    public void Transform_WhenACredentialSitsInsideAPluginsOwnJson_RewritesIt()
    {
        var config = PluginStoring("""{"host":"example.test","token":"in-the-clear"}""");

        var rewritten = SecretJsonWalker.Transform(config, SecretFields.ByName, (_, _) => "REDACTED");

        Assert.Single(rewritten);
        Assert.DoesNotContain("in-the-clear", config.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Transform_WhenACredentialSitsAJsonStringDeeperStill_RewritesIt()
    {
        // JSON inside JSON inside JSON: the scan reads only the outermost level's property names, so what saves
        // this one is that a string value opening with a brace is treated as another document to look into.
        var inner = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["profile"] = """{"token":"in-the-clear"}""",
        });

        var config = PluginStoring(inner);

        var rewritten = SecretJsonWalker.Transform(config, SecretFields.ByName, (_, _) => "REDACTED");

        Assert.Single(rewritten);
        Assert.DoesNotContain("in-the-clear", config.ToJsonString(), StringComparison.Ordinal);
    }

    // The shape `cockpit.json` keeps a plugin's settings in: its own JSON, serialised into one string value.
    private static JsonNode PluginStoring(string data) =>
        new JsonObject
        {
            ["Plugins"] = new JsonObject
            {
                ["a-plugin"] = new JsonObject { ["Data"] = new JsonObject { ["cache"] = data } },
            },
        };
}
