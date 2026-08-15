using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpenRouterProvider;

// Provider-plugin (AC-806): registers "OpenRouter" as a selectable session provider, backed by the same
// `OpenAiCompatPluginSessionDriverFactory` the Gemini/OpenAI (#45) and GitHub Models (#63) provider plugins
// use — it differs only in which OpenAI-compatible base URL a profile targets (openrouter.ai/api/v1) and in
// model notation (OpenRouter routes by vendor/model, e.g. anthropic/claude-sonnet-4.5). Chat-only
// capabilities (no tools/permissions/live model switch/plan mode/thinking) — see
// `OpenAiCompatPluginSessionDriver.Capabilities`. Declares no usage signals: OpenRouter's
// chat-completions response carries no rolling allowance/context figure this driver could read, so — same
// as Gemini/GitHub Models — a session under this provider shows no ctx-pill and no threshold warning.
public sealed class OpenRouterProviderPlugin : ICockpitPlugin
{
    // OpenRouter's OpenAI-compatible endpoint (openrouter.ai/docs/quickstart).
    internal const string OpenRouterDefaultBaseUrl = "https://openrouter.ai/api/v1";

    public PluginMetadata Metadata { get; } = new(
        Id: "openrouter-provider",
        DisplayName: "OpenRouter",
        Author: "Cockpit",
        Description: "Experimental: adds OpenRouter as a selectable session provider, over its OpenAI-compatible chat-completions endpoint via Microsoft.Extensions.AI. Chat-only — no tools, file access or permission prompts. Configure an OpenRouter API key and vendor/model id (e.g. anthropic/claude-sonnet-4.5) per profile in Manage profiles.");

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
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenRouterDefaultBaseUrl),
            DefaultBaseUrl: OpenRouterDefaultBaseUrl));
    }

    public void Dispose()
    {
    }
}
