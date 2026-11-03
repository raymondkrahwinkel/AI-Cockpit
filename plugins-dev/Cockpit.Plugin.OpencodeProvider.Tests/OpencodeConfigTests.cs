
namespace Cockpit.Plugin.OpencodeProvider.Tests;

// `OpencodeConfig`'s `ToString()` override (AC-783, mirroring Cockpit.Plugin.KimiProvider.Tests'
// KimiConfigTests-equivalent coverage): a plain `record`'s auto-generated `ToString()` would print
// `ApiKey` in the clear — a leak surface anywhere this config lands in a log line or exception message.
public class OpencodeConfigTests
{
    [Fact]
    public void ToString_RedactsTheApiKey()
    {
        var config = new OpencodeConfig(ApiKey: "sk-super-secret-key");

        var text = config.ToString();

        Assert.DoesNotContain("sk-super-secret-key", text);
        Assert.Contains("***", text);
    }
}
