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
