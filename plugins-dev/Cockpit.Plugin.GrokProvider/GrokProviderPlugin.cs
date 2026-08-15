using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GrokProvider;

// AC-724: same OpenAiCompat pattern as OpenRouter (AC-806), pointed at xAI's legacy chat-completions
// endpoint — deliberately not the newer Responses API. grok also has its own ACP-capable agent mode,
// out of scope for this ticket (route A only); see the ticket for both measurements.
public sealed class GrokProviderPlugin : ICockpitPlugin
{
    // xAI's OpenAI-compatible endpoint (docs.x.ai/docs/api-reference).
    internal const string GrokDefaultBaseUrl = "https://api.x.ai/v1";

    public PluginMetadata Metadata { get; } = new(
        Id: "grok-provider",
        DisplayName: "Grok",
        Author: "Cockpit",
        Description: "Experimental: adds Grok (xAI) as a selectable session provider, over its OpenAI-compatible legacy chat-completions endpoint via Microsoft.Extensions.AI. Chat-only — no tools, file access or permission prompts. Configure an xAI API key and model (e.g. grok-4.6) per profile in Manage profiles.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "grok-provider.grok",
            DisplayName: "Grok",
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, GrokDefaultBaseUrl),
            DefaultBaseUrl: GrokDefaultBaseUrl));
    }

    public void Dispose()
    {
    }
}
