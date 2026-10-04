using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

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

    // AC-1484: where the key comes from. A key on the profile wins; an empty one falls back to the process
    // environment (a container secret), but only on the provider's own host; neither means no sign-in. Fake values.
    [Theory]
    [InlineData("sk-config", "sk-env", OpenAiCompatDefaultBaseUrls.Gemini, PluginCredentialKind.ApiKey, "sk-config")]
    [InlineData("sk-config", null, OpenAiCompatDefaultBaseUrls.Gemini, PluginCredentialKind.ApiKey, "sk-config")]
    [InlineData("", "sk-env", OpenAiCompatDefaultBaseUrls.Gemini, PluginCredentialKind.ApiKeyFromSecret, "sk-env")]
    [InlineData("  ", "sk-env", OpenAiCompatDefaultBaseUrls.Gemini, PluginCredentialKind.ApiKeyFromSecret, "sk-env")]
    [InlineData("", null, OpenAiCompatDefaultBaseUrls.Gemini, PluginCredentialKind.Unknown, null)]
    [InlineData("", "  ", OpenAiCompatDefaultBaseUrls.Gemini, PluginCredentialKind.Unknown, null)]
    [InlineData("", "sk-env", "https://gateway.example.test/v1", PluginCredentialKind.Unknown, null)]
    [InlineData("", "sk-env", "http://generativelanguage.googleapis.com/v1", PluginCredentialKind.Unknown, null)]
    [InlineData("sk-config", "sk-env", "https://gateway.example.test/v1", PluginCredentialKind.ApiKey, "sk-config")]
    public void ApiKeySource_TakesTheProfileKeyThenTheEnvironmentOnTheOwnHostOnly(string apiKey, string? env, string baseUrl, PluginCredentialKind expectedKind, string? expectedKey)
    {
        var config = new OpenAiCompatConfig(apiKey, "model", baseUrl);
        var configJson = JsonSerializer.Serialize(config);
        var before = Environment.GetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar);
        Environment.SetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar, env);
        try
        {
            Assert.Equal(expectedKind, OpenAiCompatConfig.CredentialKindOf(configJson, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.Gemini));
            Assert.Equal(expectedKey, OpenAiCompatConfig.ResolveApiKey(config, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.Gemini));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiCompatConfig.ApiKeyEnvVar, before);
        }
    }
}
