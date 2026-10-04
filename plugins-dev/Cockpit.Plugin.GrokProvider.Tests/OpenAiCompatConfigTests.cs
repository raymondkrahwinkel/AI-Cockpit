using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

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

    // AC-1484: where the key comes from. A key on the profile wins; an empty one falls back to the process
    // environment (a container secret); neither means no sign-in. The values are fakes.
    [Theory]
    [InlineData("sk-config", "sk-env", PluginCredentialKind.ApiKey, "sk-config")]
    [InlineData("sk-config", null, PluginCredentialKind.ApiKey, "sk-config")]
    [InlineData("", "sk-env", PluginCredentialKind.ApiKeyFromSecret, "sk-env")]
    [InlineData("  ", "sk-env", PluginCredentialKind.ApiKeyFromSecret, "sk-env")]
    [InlineData("", null, PluginCredentialKind.Unknown, null)]
    [InlineData("", "  ", PluginCredentialKind.Unknown, null)]
    public void ApiKeySource_TakesTheProfileKeyThenTheEnvironment(string apiKey, string? env, PluginCredentialKind expectedKind, string? expectedKey)
    {
        var configJson = JsonSerializer.Serialize(new OpenAiCompatConfig(apiKey, "model", "https://example.test"));
        var before = Environment.GetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar);
        Environment.SetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar, env);
        try
        {
            Assert.Equal(expectedKind, OpenAiCompatConfig.CredentialKindOf(configJson, OpenAiCompatConfig.ApiKeyEnvVar));
            Assert.Equal(expectedKey, OpenAiCompatConfig.ResolveApiKey(apiKey, OpenAiCompatConfig.ApiKeyEnvVar));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar, before);
        }
    }
}
