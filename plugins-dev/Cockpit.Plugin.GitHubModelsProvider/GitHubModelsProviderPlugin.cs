using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubModelsProvider;

/// <summary>
/// Provider-plugin (#63): registers "GitHub Models" as a selectable session provider, backed by the same
/// <see cref="OpenAiCompatPluginSessionDriverFactory"/> the Gemini/OpenAI provider plugin (#45) uses — it
/// differs only in which OpenAI-compatible base URL a profile targets (models.github.ai/inference) and in
/// auth (a GitHub PAT with the models:read scope, not a vendor API key). Chat-only capabilities (no
/// tools/permissions/live model switch/plan mode/thinking) — see
/// <see cref="OpenAiCompatPluginSessionDriver.Capabilities"/>. This is GitHub Models, not GitHub Copilot —
/// there is no officially supported "Copilot" chat model via this endpoint (see design doc #63a); naming and
/// help text in this plugin deliberately avoid the "Copilot" label to prevent that confusion.
/// </summary>
public sealed class GitHubModelsProviderPlugin : ICockpitPlugin
{
    /// <summary>GitHub Models' OpenAI-compatible inference endpoint (docs.github.com/rest/models/inference).</summary>
    internal const string GitHubModelsDefaultBaseUrl = "https://models.github.ai/inference";

    public PluginMetadata Metadata { get; } = new(
        Id: "github-models-provider",
        DisplayName: "GitHub Models",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Experimental: adds GitHub Models as a selectable session provider, over its OpenAI-compatible chat-completions endpoint via Microsoft.Extensions.AI. Configure a GitHub personal access token (models:read scope) and model per profile in Manage profiles.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "github-models-provider.github-models",
            DisplayName: "GitHub Models",
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, GitHubModelsDefaultBaseUrl),
            DefaultBaseUrl: GitHubModelsDefaultBaseUrl));
    }

    public void Dispose()
    {
    }
}
