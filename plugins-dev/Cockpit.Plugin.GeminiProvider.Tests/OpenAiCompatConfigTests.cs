
namespace Cockpit.Plugin.GeminiProvider.Tests;

/// <summary>
/// <see cref="OpenAiCompatConfig"/>'s <c>ToString()</c> override (#45 review finding 4): a plain
/// <c>record</c>'s auto-generated <c>ToString()</c> would print <see cref="OpenAiCompatConfig.ApiKey"/> in
/// the clear — a leak surface anywhere this config lands in a log line or exception message (e.g. the
/// <see cref="OpenAiCompatPluginSessionDriverFactory"/> deserialize-failure path).
/// </summary>
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
