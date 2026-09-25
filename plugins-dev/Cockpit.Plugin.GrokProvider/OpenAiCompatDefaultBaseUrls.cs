namespace Cockpit.Plugin.GrokProvider;

// The endpoint this plugin's provider targets — its own file (AC-1393) so the UI part's config view can read
// the same value as the backend's registration without linking the whole plugin class.
internal static class OpenAiCompatDefaultBaseUrls
{
    // xAI's OpenAI-compatible endpoint (docs.x.ai/docs/api-reference).
    public const string Grok = "https://api.x.ai/v1";
}
