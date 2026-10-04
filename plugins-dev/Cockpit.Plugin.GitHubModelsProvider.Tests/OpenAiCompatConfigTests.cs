using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

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

    // AC-1484: where the key comes from. A key on the profile wins; an empty one falls back to the process
    // environment (a container secret), but only on the provider's own host; neither means no sign-in. Fake values.
    [Theory]
    [InlineData("sk-config", "sk-env", OpenAiCompatDefaultBaseUrls.GitHubModels, PluginCredentialKind.ApiKey, "sk-config")]
    [InlineData("sk-config", null, OpenAiCompatDefaultBaseUrls.GitHubModels, PluginCredentialKind.ApiKey, "sk-config")]
    [InlineData("", "sk-env", OpenAiCompatDefaultBaseUrls.GitHubModels, PluginCredentialKind.ApiKeyFromSecret, "sk-env")]
    [InlineData("  ", "sk-env", OpenAiCompatDefaultBaseUrls.GitHubModels, PluginCredentialKind.ApiKeyFromSecret, "sk-env")]
    [InlineData("", null, OpenAiCompatDefaultBaseUrls.GitHubModels, PluginCredentialKind.Unknown, null)]
    [InlineData("", "  ", OpenAiCompatDefaultBaseUrls.GitHubModels, PluginCredentialKind.Unknown, null)]
    [InlineData("", "sk-env", "https://gateway.example.test/v1", PluginCredentialKind.Unknown, null)]
    [InlineData("", "sk-env", "http://models.github.ai/v1", PluginCredentialKind.Unknown, null)]
    [InlineData("sk-config", "sk-env", "https://gateway.example.test/v1", PluginCredentialKind.ApiKey, "sk-config")]
    public void ApiKeySource_TakesTheProfileKeyThenTheEnvironmentOnTheOwnHostOnly(string apiKey, string? env, string baseUrl, PluginCredentialKind expectedKind, string? expectedKey)
    {
        // The generic GITHUB_TOKEN is what gh and git use; it must never be taken for a Models key.
        Assert.Equal("GITHUB_MODELS_TOKEN", OpenAiCompatConfig.ApiKeyEnvVar);
        var config = new OpenAiCompatConfig(apiKey, "model", baseUrl);
        var configJson = JsonSerializer.Serialize(config);
        var before = Environment.GetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar);
        Environment.SetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar, env);
        try
        {
            Assert.Equal(expectedKind, OpenAiCompatConfig.CredentialKindOf(configJson, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.GitHubModels));
            Assert.Equal(expectedKey, OpenAiCompatConfig.ResolveApiKey(config, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.GitHubModels));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar, before);
        }
    }
}
