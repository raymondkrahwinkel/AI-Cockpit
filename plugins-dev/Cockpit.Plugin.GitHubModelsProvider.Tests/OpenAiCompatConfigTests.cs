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
