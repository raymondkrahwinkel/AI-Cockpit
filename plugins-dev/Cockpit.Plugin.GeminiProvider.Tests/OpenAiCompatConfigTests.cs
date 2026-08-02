
namespace Cockpit.Plugin.GeminiProvider.Tests;

// `OpenAiCompatConfig`'s `ToString()` override (#45 review finding 4): a plain
// `record`'s auto-generated `ToString()` would print `OpenAiCompatConfig.ApiKey` in
// the clear — a leak surface anywhere this config lands in a log line or exception message (e.g. the
// `OpenAiCompatPluginSessionDriverFactory` deserialize-failure path).
public class OpenAiCompatConfigTests
{
    [Fact]
    public void ToString_RedactsTheApiKey()
    {
        var config = new OpenAiCompatConfig("super-secret-key", "gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta/openai/");

        var text = config.ToString();

        Assert.DoesNotContain("super-secret-key", text);
        Assert.Contains("***", text);
        Assert.Contains("gemini-2.5-flash", text);
        Assert.Contains("https://generativelanguage.googleapis.com/v1beta/openai/", text);
    }
}
