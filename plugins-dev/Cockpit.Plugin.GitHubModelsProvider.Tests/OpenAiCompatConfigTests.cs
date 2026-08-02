
namespace Cockpit.Plugin.GitHubModelsProvider.Tests;

// `OpenAiCompatConfig`'s `ToString()` override (#63, mirroring the Gemini/OpenAI provider
// plugin's #45 review finding 4): a plain `record`'s auto-generated `ToString()` would print
// `OpenAiCompatConfig.ApiKey` (a GitHub PAT here) in the clear — a leak surface anywhere this
// config lands in a log line or exception message (e.g. the
// `OpenAiCompatPluginSessionDriverFactory` deserialize-failure path).
public class OpenAiCompatConfigTests
{
    [Fact]
    public void ToString_RedactsTheApiKey()
    {
        var config = new OpenAiCompatConfig("github_pat_super-secret-token", "openai/gpt-4.1", "https://models.github.ai/inference");

        var text = config.ToString();

        Assert.DoesNotContain("github_pat_super-secret-token", text);
        Assert.Contains("***", text);
        Assert.Contains("openai/gpt-4.1", text);
        Assert.Contains("https://models.github.ai/inference", text);
    }

    [Fact]
    public void ToString_WithAnEmptyApiKey_PrintsNullInsteadOfAsterisks()
    {
        var config = new OpenAiCompatConfig(string.Empty, "openai/gpt-4.1", "https://models.github.ai/inference");

        var text = config.ToString();

        Assert.Contains("ApiKey = null", text);
        Assert.DoesNotContain("***", text);
    }
}
