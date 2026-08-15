
namespace Cockpit.Plugin.OpenRouterProvider.Tests;

// `OpenAiCompatConfig`'s `ToString()` override (AC-806, mirroring the GitHub Models provider
// plugin's #63 review finding): a plain `record`'s auto-generated `ToString()` would print
// `OpenAiCompatConfig.ApiKey` (an OpenRouter API key here) in the clear — a leak surface anywhere this
// config lands in a log line or exception message (e.g. the
// `OpenAiCompatPluginSessionDriverFactory` deserialize-failure path).
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
