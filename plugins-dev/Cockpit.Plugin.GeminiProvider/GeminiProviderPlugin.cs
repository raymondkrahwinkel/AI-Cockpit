using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GeminiProvider;

// Fase A worked example provider-plugin (#45): registers two session providers, Gemini and OpenAI, both
// backed by the same `OpenAiCompatPluginSessionDriverFactory` — they differ only in which
// OpenAI-compatible base URL a profile targets. Tools come from the host's own loop (AC-964); no permissions/live model
// switch/plan mode/thinking) — see `OpenAiCompatPluginSessionDriver.Capabilities`.
public sealed class GeminiProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "gemini-provider",
        DisplayName: "Gemini / OpenAI Provider",
        Author: "Cockpit",
        Description: "Adds Gemini and OpenAI as selectable session providers, both over an OpenAI-compatible chat-completions endpoint via Microsoft.Extensions.AI. Runs the session's MCP tools through the cockpit, which gates every call. Configure an API key and model per profile in Manage profiles.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "gemini-provider.gemini",
            DisplayName: "Gemini (OpenAI-compatible)",
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(host, OpenAiCompatConfig.ApiKeyEnvVar),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false)
            {
                // AC-964: this endpoint is plain chat completions — it brings no tool search of its own, so the
                // host's may ride along without ever giving the model two ways to find the same tool. The driver
                // flips SupportsTools once a session actually gets tools; the registration cannot know that yet.
                HostToolLoop = PluginHostToolLoop.ToolsAndSearch,
            },
            DefaultBaseUrl: OpenAiCompatDefaultBaseUrls.Gemini)
        {
            TermsNotice = new TermsNotice("Gemini (Google): API-key use is billed to the key owner and governed by Google's API terms. A custom endpoint is governed by its own service agreement.", new Uri("https://ai.google.dev/gemini-api/terms")),
            // AC-1484: a key on the profile, or the provider variable in session.env, is what signs a profile in.
            IsLoggedIn = configJson => OpenAiCompatConfig.CredentialKindOf(configJson, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.Gemini) != PluginCredentialKind.Unknown,
            CredentialKind = configJson => OpenAiCompatConfig.CredentialKindOf(configJson, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.Gemini),
            ReadModelIntelligenceAsync = (configJson, _) => Task.FromResult(_ReadModelIntelligence(configJson, OpenAiCompatDefaultBaseUrls.Gemini)),
        });

        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "gemini-provider.openai",
            DisplayName: "OpenAI",
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(host, null),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false)
            {
                // AC-964: this endpoint is plain chat completions — it brings no tool search of its own, so the
                // host's may ride along without ever giving the model two ways to find the same tool. The driver
                // flips SupportsTools once a session actually gets tools; the registration cannot know that yet.
                HostToolLoop = PluginHostToolLoop.ToolsAndSearch,
            },
            DefaultBaseUrl: OpenAiCompatDefaultBaseUrls.OpenAi)
        {
            TermsNotice = new TermsNotice("OpenAI / OpenAI-compatible: API-key use is billed to the key owner. OpenAI API terms apply to OpenAI endpoints; Hetzner endpoints are governed by your Hetzner agreement. Other endpoints have their own service terms.", new Uri("https://openai.com/policies/services-agreement/"), ApiTermsUrl: new Uri("https://www.hetzner.com/legal/terms-and-conditions/")),
            ReadModelIntelligenceAsync = (configJson, _) => Task.FromResult(_ReadModelIntelligence(configJson, OpenAiCompatDefaultBaseUrls.OpenAi)),
        });
    }

    private static PluginModelIntelligence _ReadModelIntelligence(string configJson, string defaultBaseUrl)
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
            IsSelfHosted = config.IsSelfHosted,
            Models = [new PluginModelCostEstimate(config.Model) { PublicModelRef = config.Model }],
        };
    }

    public void Dispose()
    {
    }
}
