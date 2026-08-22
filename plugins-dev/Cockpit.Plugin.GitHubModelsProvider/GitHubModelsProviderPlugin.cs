using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubModelsProvider;

// Provider-plugin (#63): registers "GitHub Models" as a selectable session provider, backed by the same
// `OpenAiCompatPluginSessionDriverFactory` the Gemini/OpenAI provider plugin (#45) uses — it
// differs only in which OpenAI-compatible base URL a profile targets (models.github.ai/inference) and in
// auth (a GitHub PAT with the models:read scope, not a vendor API key). Tools come from the host's own loop (AC-964); no
// tools/permissions/live model switch/plan mode/thinking) — see
// `OpenAiCompatPluginSessionDriver.Capabilities`. This is GitHub Models, not GitHub Copilot —
// there is no officially supported "Copilot" chat model via this endpoint (see design doc #63a); naming and
// help text in this plugin deliberately avoid the "Copilot" label to prevent that confusion.
public sealed class GitHubModelsProviderPlugin : ICockpitPlugin
{
    // GitHub Models' OpenAI-compatible inference endpoint (docs.github.com/rest/models/inference).
    internal const string GitHubModelsDefaultBaseUrl = "https://models.github.ai/inference";

    public PluginMetadata Metadata { get; } = new(
        Id: "github-models-provider",
        DisplayName: "GitHub Models",
        Author: "Cockpit",
        Description: "Experimental: adds GitHub Models as a selectable session provider, over its OpenAI-compatible chat-completions endpoint via Microsoft.Extensions.AI. Runs the session's MCP tools through the cockpit, which gates every call. Configure a GitHub personal access token (models:read scope) and model per profile in Manage profiles.");

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
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false)
            {
                // AC-964: this endpoint is plain chat completions — it brings no tool search of its own, so the
                // host's may ride along without ever giving the model two ways to find the same tool. The driver
                // flips SupportsTools once a session actually gets tools; the registration cannot know that yet.
                HostToolLoop = PluginHostToolLoop.ToolsAndSearch,
            },
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, GitHubModelsDefaultBaseUrl, host),
            DefaultBaseUrl: GitHubModelsDefaultBaseUrl));
    }

    public void Dispose()
    {
    }
}
