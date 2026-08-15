
namespace Cockpit.Plugin.OpenRouterProvider.Tests;

// AC-806: `OpenAiCompatConfig.ToString()` must redact ApiKey — a plain record's auto-generated override
// would print it in the clear anywhere this config lands in a log line or exception message.
public class OpenAiCompatConfigTests
{
    [Fact]
    public void ToString_RedactsTheApiKey()
    {
        var config = new OpenAiCompatConfig("sk-or-v1-super-secret-key", "anthropic/claude-sonnet-4.5", "https://openrouter.ai/api/v1");

        var text = config.ToString();

        Assert.DoesNotContain("sk-or-v1-super-secret-key", text);
        Assert.Contains("***", text);
        Assert.Contains("anthropic/claude-sonnet-4.5", text);
        Assert.Contains("https://openrouter.ai/api/v1", text);
    }

    [Fact]
    public void ToString_WithAnEmptyApiKey_PrintsNullInsteadOfAsterisks()
    {
        var config = new OpenAiCompatConfig(string.Empty, "anthropic/claude-sonnet-4.5", "https://openrouter.ai/api/v1");

        var text = config.ToString();

        Assert.Contains("ApiKey = null", text);
        Assert.DoesNotContain("***", text);
    }
}
