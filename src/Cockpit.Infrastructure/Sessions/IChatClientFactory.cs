using Microsoft.Extensions.AI;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Builds a Microsoft.Extensions.AI <see cref="IChatClient"/> for a local OpenAI-compatible provider
/// (Ollama/LM Studio) from its profile config. A seam so <see cref="OpenAiCompatSessionDriver"/> can be
/// unit-tested against a fake chat client without a running server.
/// </summary>
internal interface IChatClientFactory
{
    IChatClient Create(ProviderConfig config);

    /// <summary>
    /// Builds an <see cref="IChatClient"/> straight from a resolved OpenAI-compatible endpoint (base URL without
    /// the <c>/v1</c> suffix, plus a model id), for callers that already know the endpoint rather than a profile
    /// <see cref="ProviderConfig"/> — e.g. the shared voice-LLM cleanup/naturalize/summarize path.
    /// </summary>
    IChatClient CreateForEndpoint(string baseUrl, string model, string? apiKey = null);
}
