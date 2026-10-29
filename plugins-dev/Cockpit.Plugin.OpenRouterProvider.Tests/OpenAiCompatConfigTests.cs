using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Plugin.OpenRouterProvider.Tests;

// AC-806: `OpenAiCompatConfig.ToString()` must redact ApiKey — a plain record's auto-generated override
// would print it in the clear anywhere this config lands in a log line or exception message.
public class OpenAiCompatConfigTests
{
    [Fact]
    public async Task ToString_RedactsTheApiKey_AndThePluginReportsItsConfiguredOwnEndpointModel()
    {
        var config = new OpenAiCompatConfig("sk-or-v1-super-secret-key", "anthropic/claude-sonnet-4.5", "https://openrouter.ai/api/v1");

        var text = config.ToString();

        Assert.DoesNotContain("sk-or-v1-super-secret-key", text);
        Assert.Contains("***", text);
        Assert.Contains("anthropic/claude-sonnet-4.5", text);
        Assert.Contains("https://openrouter.ai/api/v1", text);

        var host = Substitute.For<ICockpitHost>();
        SessionProviderRegistration? registration = null;
        host.When(value => value.AddSessionProvider(Arg.Any<SessionProviderRegistration>()))
            .Do(call => registration = call.Arg<SessionProviderRegistration>());
        new OpenRouterProviderPlugin().Initialize(host);

        Assert.NotNull(registration);
        Assert.NotNull(registration.ReadModelIntelligenceAsync);
        var ownEndpoint = JsonSerializer.Serialize(new OpenAiCompatConfig("key", "Qwen3.8-27B", "https://hetzner.example/v1"));
        var reading = await registration.ReadModelIntelligenceAsync!(ownEndpoint, CancellationToken.None);

        Assert.True(reading.IsSelfHosted);
        var model = Assert.Single(reading.Models);
        Assert.Equal("Qwen3.8-27B", model.Model);
        Assert.Equal("Qwen3.8-27B", model.PublicModelRef);
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
