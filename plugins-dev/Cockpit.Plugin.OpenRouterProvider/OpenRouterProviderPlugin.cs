using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpenRouterProvider;

// AC-806: registers "OpenRouter" as a selectable session provider on the same OpenAiCompat driver the
// Gemini/GitHub Models plugins use — with the host's tool loop (AC-964), and declares no usage signals since OpenRouter's
// chat-completions response carries no rolling allowance/context figure to read.
public sealed class OpenRouterProviderPlugin : ICockpitPlugin
{
    // OpenRouter's OpenAI-compatible endpoint (openrouter.ai/docs/quickstart).
    internal const string OpenRouterDefaultBaseUrl = "https://openrouter.ai/api/v1";

    public PluginMetadata Metadata { get; } = new(
        Id: "openrouter-provider",
        DisplayName: "OpenRouter",
        Author: "Cockpit",
        Description: "Experimental: adds OpenRouter as a selectable session provider, over its OpenAI-compatible chat-completions endpoint via Microsoft.Extensions.AI. Runs the session's MCP tools through the cockpit, which gates every call. Configure an OpenRouter API key and vendor/model id (e.g. anthropic/claude-sonnet-4.5) per profile in Manage profiles.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "openrouter-provider.openrouter",
            DisplayName: "OpenRouter",
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false)
            {
                // AC-964: this endpoint is plain chat completions — it brings no tool search of its own, so the
                // host's may ride along without ever giving the model two ways to find the same tool. The driver
                // flips SupportsTools once a session actually gets tools; the registration cannot know that yet.
                HostToolLoop = PluginHostToolLoop.ToolsAndSearch,
            },
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenRouterDefaultBaseUrl),
            DefaultBaseUrl: OpenRouterDefaultBaseUrl));
    }

    public void Dispose()
    {
    }
}
