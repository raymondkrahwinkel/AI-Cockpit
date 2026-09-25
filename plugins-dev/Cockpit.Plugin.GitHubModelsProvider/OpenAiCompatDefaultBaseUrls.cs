namespace Cockpit.Plugin.GitHubModelsProvider;

// The endpoint this plugin's provider targets — its own file (AC-1393) so the UI part's config view can read
// the same value as the backend's registration without linking the whole plugin class.
internal static class OpenAiCompatDefaultBaseUrls
{
    // GitHub Models' OpenAI-compatible inference endpoint (docs.github.com/rest/models/inference).
    public const string GitHubModels = "https://models.github.ai/inference";
}
