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
    // Gemini's OpenAI-compatible endpoint (ai.google.dev/gemini-api/docs/openai).
    internal const string GeminiDefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";

    // OpenAI's own Chat Completions endpoint.
    internal const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1";

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
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false)
            {
                // AC-964: this endpoint is plain chat completions — it brings no tool search of its own, so the
                // host's may ride along without ever giving the model two ways to find the same tool. The driver
                // flips SupportsTools once a session actually gets tools; the registration cannot know that yet.
                HostToolLoop = PluginHostToolLoop.ToolsAndSearch,
            },
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, GeminiDefaultBaseUrl),
            DefaultBaseUrl: GeminiDefaultBaseUrl));

        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "gemini-provider.openai",
            DisplayName: "OpenAI",
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false)
            {
                // AC-964: this endpoint is plain chat completions — it brings no tool search of its own, so the
                // host's may ride along without ever giving the model two ways to find the same tool. The driver
                // flips SupportsTools once a session actually gets tools; the registration cannot know that yet.
                HostToolLoop = PluginHostToolLoop.ToolsAndSearch,
            },
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenAiDefaultBaseUrl),
            DefaultBaseUrl: OpenAiDefaultBaseUrl));
    }

    public void Dispose()
    {
    }
}
