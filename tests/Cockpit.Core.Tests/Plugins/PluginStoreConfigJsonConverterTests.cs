using System.Text.Json;
using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The string-or-object JSON shape of a <see cref="PluginStoreConfig"/> (AC-7): a bare URL string from a pre-AC-7 config still reads, an object round-trips, and the token is written under the secret-scrubbed name.</summary>
public class PluginStoreConfigJsonConverterTests
{
    [Fact]
    public void Read_RemoteObjectWithToken_KeepsTheToken_AndToStringRedactsIt()
    {
        var store = JsonSerializer.Deserialize<PluginStoreConfig>(
            """{ "kind": "remote", "location": "https://github.com/a/b", "token": "abc123" }""");

        Assert.Equal(new PluginStoreConfig(PluginStoreKind.Remote, "https://github.com/a/b", "abc123"), store);

        var text = store!.ToString();
        Assert.DoesNotContain("abc123", text);
        Assert.Contains("***", text);
    }
}
