namespace Cockpit.Plugin.GeminiProvider;

// The OpenAI-compatible endpoints this plugin's named providers target — their own file (AC-1393) so the UI part's
// config view can read the same values as the backend's registration without linking the whole plugin class.
internal static class OpenAiCompatDefaultBaseUrls
{
    // Gemini's OpenAI-compatible endpoint (ai.google.dev/gemini-api/docs/openai).
    public const string Gemini = "https://generativelanguage.googleapis.com/v1beta/openai/";

    // OpenAI's own Chat Completions endpoint.
    public const string OpenAi = "https://api.openai.com/v1";

    // console.groq.com/docs/openai
    public const string Groq = "https://api.groq.com/openai/v1";

    // docs.mistral.ai/api/endpoint/chat
    public const string Mistral = "https://api.mistral.ai/v1";

    // inference-docs.cerebras.ai/resources/openai
    public const string Cerebras = "https://api.cerebras.ai/v1";
}
