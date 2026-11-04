using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GeminiProvider;

// Worked example provider-plugin (#45): registers every provider in `OpenAiCompatProvider.All`, all
// backed by the same `OpenAiCompatPluginSessionDriverFactory` — they differ only in which
// OpenAI-compatible base URL a profile targets. Tools come from the host's own loop (AC-964); no permissions/live model
// switch/plan mode/thinking) — see `OpenAiCompatPluginSessionDriver.Capabilities`.
public sealed class GeminiProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "gemini-provider",
        DisplayName: "OpenAI-compatible",
        Author: "Cockpit",
        Description: "Connects any OpenAI-compatible chat-completions endpoint as a session provider via Microsoft.Extensions.AI, with ready entries for Gemini, OpenAI, Groq, Mistral and Cerebras, and a custom base URL for the rest. Runs the session's MCP tools through the cockpit, which gates every call. Configure an API key and model per profile in Manage profiles.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        foreach (var provider in OpenAiCompatProvider.All)
        {
            host.AddSessionProvider(_Registration(host, provider));
        }
    }

    private static SessionProviderRegistration _Registration(ICockpitHost host, OpenAiCompatProvider provider)
    {
        var registration = new SessionProviderRegistration(
            ProviderId: provider.ProviderId,
            DisplayName: provider.DisplayName,
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(host, provider.ApiKeyEnvVar),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false, SupportsVision: true)
            {
                // AC-964: this endpoint is plain chat completions — it brings no tool search of its own, so the
                // host's may ride along without ever giving the model two ways to find the same tool. The driver
                // flips SupportsTools once a session actually gets tools; the registration cannot know that yet.
                HostToolLoop = PluginHostToolLoop.ToolsAndSearch,
            },
            DefaultBaseUrl: provider.BaseUrl)
        {
            TermsNotice = provider.Terms,
            ReadModelIntelligenceAsync = (configJson, _) => Task.FromResult(_ReadModelIntelligence(configJson, provider)),
        };

        // AC-1484: a key on the profile, or the provider variable in session.env, is what signs a profile in.
        return provider.ApiKeyEnvVar is null ? registration : registration with
        {
            IsLoggedIn = configJson => OpenAiCompatConfig.CredentialKindOf(configJson, provider.ApiKeyEnvVar, provider.BaseUrl) != PluginCredentialKind.Unknown,
            CredentialKind = configJson => OpenAiCompatConfig.CredentialKindOf(configJson, provider.ApiKeyEnvVar, provider.BaseUrl),
        };
    }

    private static PluginModelIntelligence _ReadModelIntelligence(string configJson, OpenAiCompatProvider provider)
    {
        OpenAiCompatConfig? config;
        try
        {
            config = System.Text.Json.JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, OpenAiCompatConfig.JsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return new PluginModelIntelligence();
        }

        if (config is null || string.IsNullOrWhiteSpace(config.Model))
        {
            return new PluginModelIntelligence();
        }

        return new PluginModelIntelligence
        {
            IsSelfHosted = config.IsSelfHosted || provider.IsCustom,
            Models = [new PluginModelCostEstimate(config.Model) { PublicModelRef = config.Model }],
        };
    }

    public void Dispose()
    {
    }
}
