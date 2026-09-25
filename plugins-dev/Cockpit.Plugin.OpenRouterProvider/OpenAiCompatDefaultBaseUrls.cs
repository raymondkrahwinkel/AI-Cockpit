namespace Cockpit.Plugin.OpenRouterProvider;

// The endpoint this plugin's provider targets — its own file (AC-1393) so the UI part's config view can read
// the same value as the backend's registration without linking the whole plugin class.
internal static class OpenAiCompatDefaultBaseUrls
{
    // OpenRouter's OpenAI-compatible endpoint (openrouter.ai/docs/quickstart).
    public const string OpenRouter = "https://openrouter.ai/api/v1";
}
