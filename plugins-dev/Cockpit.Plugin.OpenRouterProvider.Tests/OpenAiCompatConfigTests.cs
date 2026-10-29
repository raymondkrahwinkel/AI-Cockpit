using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

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

    // AC-1484: where the key comes from. A key on the profile wins; an empty one falls back to the process
    // environment (a container secret), but only on the provider's own host; neither means no sign-in. Fake values.
    [Theory]
    [InlineData("sk-config", "sk-env", OpenAiCompatDefaultBaseUrls.OpenRouter, PluginCredentialKind.ApiKey, "sk-config")]
    [InlineData("sk-config", null, OpenAiCompatDefaultBaseUrls.OpenRouter, PluginCredentialKind.ApiKey, "sk-config")]
    [InlineData("", "sk-env", OpenAiCompatDefaultBaseUrls.OpenRouter, PluginCredentialKind.ApiKeyFromSecret, "sk-env")]
    [InlineData("  ", "sk-env", OpenAiCompatDefaultBaseUrls.OpenRouter, PluginCredentialKind.ApiKeyFromSecret, "sk-env")]
    [InlineData("", null, OpenAiCompatDefaultBaseUrls.OpenRouter, PluginCredentialKind.Unknown, null)]
    [InlineData("", "  ", OpenAiCompatDefaultBaseUrls.OpenRouter, PluginCredentialKind.Unknown, null)]
    [InlineData("", "sk-env", "https://gateway.example.test/v1", PluginCredentialKind.Unknown, null)]
    [InlineData("", "sk-env", "http://openrouter.ai/v1", PluginCredentialKind.Unknown, null)]
    [InlineData("sk-config", "sk-env", "https://gateway.example.test/v1", PluginCredentialKind.ApiKey, "sk-config")]
    public void ApiKeySource_TakesTheProfileKeyThenTheEnvironmentOnTheOwnHostOnly(string apiKey, string? env, string baseUrl, PluginCredentialKind expectedKind, string? expectedKey)
    {
        var config = new OpenAiCompatConfig(apiKey, "model", baseUrl);
        var configJson = JsonSerializer.Serialize(config);
        var before = Environment.GetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar);
        Environment.SetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar, env);
        try
        {
            Assert.Equal(expectedKind, OpenAiCompatConfig.CredentialKindOf(configJson, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.OpenRouter));
            Assert.Equal(expectedKey, OpenAiCompatConfig.ResolveApiKey(config, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.OpenRouter));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar, before);
        }
    }
}
