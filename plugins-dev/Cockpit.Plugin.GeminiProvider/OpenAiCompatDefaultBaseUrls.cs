namespace Cockpit.Plugin.GeminiProvider;

// The two OpenAI-compatible endpoints this plugin's providers target — their own file (AC-1393) so the UI part's
// config view can read the same values as the backend's registration without linking the whole plugin class.
internal static class OpenAiCompatDefaultBaseUrls
{
    // Gemini's OpenAI-compatible endpoint (ai.google.dev/gemini-api/docs/openai).
    public const string Gemini = "https://generativelanguage.googleapis.com/v1beta/openai/";

    // OpenAI's own Chat Completions endpoint.
    public const string OpenAi = "https://api.openai.com/v1";
}
