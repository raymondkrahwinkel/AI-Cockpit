using System.Text.Json;

namespace Cockpit.Plugin.GeminiProvider.Tests;

// AC-1344: TimeoutSeconds is the smallest knob for the NetworkTimeout that made Hetzner Inference
// (Qwen3.8-27B, gemini-provider.openai) fail every turn after the first tool round. This proves the value
// a profile's config JSON carries actually lands on the pipeline option the OpenAI SDK reads for it.
public class OpenAiCompatPluginSessionDriverFactoryTests
{
    [Theory]
    [InlineData("""{"ApiKey":"key","Model":"model","BaseUrl":"https://example.test","TimeoutSeconds":45}""", 45)]
    [InlineData("""{"ApiKey":"key","Model":"model","BaseUrl":"https://example.test"}""", OpenAiCompatConfig.DefaultTimeoutSeconds)]
    public void BuildClientOptions_NetworkTimeout_UsesTheConfiguredValueOrTheDefault(string configJson, int expectedSeconds)
    {
        var config = JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, OpenAiCompatConfig.JsonOptions)
            ?? throw new InvalidOperationException("The config JSON did not deserialize.");

        var options = OpenAiCompatPluginSessionDriverFactory.BuildClientOptions(config);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.NetworkTimeout);
    }
}
