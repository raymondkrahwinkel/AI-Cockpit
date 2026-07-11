using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GeminiProvider;

/// <summary>
/// Fase A worked example provider-plugin (#45): registers two session providers, Gemini and OpenAI, both
/// backed by the same <see cref="OpenAiCompatPluginSessionDriverFactory"/> — they differ only in which
/// OpenAI-compatible base URL a profile targets. Chat-only capabilities (no tools/permissions/live model
/// switch/plan mode/thinking) — see <see cref="OpenAiCompatPluginSessionDriver.Capabilities"/>.
/// </summary>
public sealed class GeminiProviderPlugin : ICockpitPlugin
{
    /// <summary>Gemini's OpenAI-compatible endpoint (ai.google.dev/gemini-api/docs/openai).</summary>
    internal const string GeminiDefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";

    /// <summary>OpenAI's own Chat Completions endpoint.</summary>
    internal const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1";

    public PluginMetadata Metadata { get; } = new(
        Id: "gemini-provider",
        DisplayName: "Gemini / OpenAI Provider",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Adds Gemini and OpenAI as selectable session providers, both over an OpenAI-compatible chat-completions endpoint via Microsoft.Extensions.AI. Configure an API key and model per profile in Manage profiles.");

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
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, GeminiDefaultBaseUrl),
            DefaultBaseUrl: GeminiDefaultBaseUrl));

        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "gemini-provider.openai",
            DisplayName: "OpenAI",
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenAiDefaultBaseUrl),
            DefaultBaseUrl: OpenAiDefaultBaseUrl));
    }

    public void Dispose()
    {
    }
}
