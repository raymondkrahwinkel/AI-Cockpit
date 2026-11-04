using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GeminiProvider;

// Every provider this plugin registers, shared with the UI part. A ProviderId is stored in profiles, so it never
// changes; Hetzner Inference profiles live on "gemini-provider.openai" with their own base URL.
internal sealed record OpenAiCompatProvider(string ProviderId, string DisplayName, string BaseUrl, string Supports, TermsNotice Terms, string? ApiKeyEnvVar = null)
{
    private const string Standard = "Chat, streaming and tools through the cockpit. Images depend on the model. Token usage, reasoning and cache hits are not shown.";

    public bool IsCustom => BaseUrl.Length == 0;

    public static readonly IReadOnlyList<OpenAiCompatProvider> All =
    [
        new(
            "gemini-provider.gemini",
            "Gemini (OpenAI-compatible)",
            OpenAiCompatDefaultBaseUrls.Gemini,
            // Gemini's docs require thought signatures for multi-turn function calling; the shared driver drops them.
            "Chat and streaming. Tool calls may fail on Gemini 3 models. Images depend on the model. Token usage, thoughts and cache hits are not shown.",
            new TermsNotice("Gemini (Google): API-key use is billed to the key owner and governed by Google's API terms. A custom endpoint is governed by its own service agreement.", new Uri("https://ai.google.dev/gemini-api/terms")),
            OpenAiCompatConfig.ApiKeyEnvVar),
        new(
            "gemini-provider.openai",
            "OpenAI",
            OpenAiCompatDefaultBaseUrls.OpenAi,
            Standard,
            new TermsNotice("OpenAI / OpenAI-compatible: API-key use is billed to the key owner. OpenAI API terms apply to OpenAI endpoints; Hetzner endpoints are governed by your Hetzner agreement. Other endpoints have their own service terms.", new Uri("https://openai.com/policies/services-agreement/"), ApiTermsUrl: new Uri("https://www.hetzner.com/legal/terms-and-conditions/"))),
        new(
            "gemini-provider.groq",
            "Groq",
            OpenAiCompatDefaultBaseUrls.Groq,
            Standard,
            new TermsNotice("Groq: API-key use is billed to the key owner and governed by Groq's terms.", new Uri("https://groq.com/terms-of-use"))),
        new(
            "gemini-provider.mistral",
            "Mistral",
            OpenAiCompatDefaultBaseUrls.Mistral,
            Standard,
            new TermsNotice("Mistral: API-key use is billed to the key owner and governed by Mistral's terms.", new Uri("https://legal.mistral.ai/terms/commercial-terms-of-service"))),
        new(
            "gemini-provider.cerebras",
            "Cerebras",
            OpenAiCompatDefaultBaseUrls.Cerebras,
            Standard,
            new TermsNotice("Cerebras: API-key use is billed to the key owner and governed by Cerebras's terms.", new Uri("https://www.cerebras.ai/terms-of-service"))),
        new(
            "gemini-provider.custom",
            "Custom (OpenAI-compatible)",
            string.Empty,
            "For any endpoint with OpenAI chat completions. Chat and streaming work; tools and images depend on the endpoint and model.",
            new TermsNotice("Custom endpoint: use is governed by that endpoint's own service agreement.", new Uri("https://platform.openai.com/docs/api-reference/chat"))),
    ];
}
