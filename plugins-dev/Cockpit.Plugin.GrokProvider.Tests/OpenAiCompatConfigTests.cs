
namespace Cockpit.Plugin.GrokProvider.Tests;

// AC-724: `OpenAiCompatConfig.ToString()` must redact ApiKey — a plain record's auto-generated override
// would print it in the clear anywhere this config lands in a log line or exception message.
public class OpenAiCompatConfigTests
{
    [Fact]
    public void ToString_RedactsTheApiKey()
    {
        var config = new OpenAiCompatConfig("xai-super-secret-key", "grok-4.6", "https://api.x.ai/v1");

        var text = config.ToString();

        Assert.DoesNotContain("xai-super-secret-key", text);
        Assert.Contains("***", text);
        Assert.Contains("grok-4.6", text);
        Assert.Contains("https://api.x.ai/v1", text);
    }

    [Fact]
    public void ToString_WithAnEmptyApiKey_PrintsNullInsteadOfAsterisks()
    {
        var config = new OpenAiCompatConfig(string.Empty, "grok-4.6", "https://api.x.ai/v1");

        var text = config.ToString();

        Assert.Contains("ApiKey = null", text);
        Assert.DoesNotContain("***", text);
    }
}
