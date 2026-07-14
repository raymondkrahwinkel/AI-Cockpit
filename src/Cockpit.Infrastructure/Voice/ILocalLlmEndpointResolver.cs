using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>The base URL + model a local-LLM call should use, once auto-detect (or the manual fallback) has decided.</summary>
internal readonly record struct LocalLlmEndpoint(string BaseUrl, string Model);

/// <summary>
/// Decides which local OpenAI-compatible server (Ollama/LM Studio) and model the cleanup/naturalize call uses.
/// With auto-detect on it reuses the process-table detection behind the memory breakdown (<c>LocalModelServers</c>,
/// #78) to find the running server, then reads its <c>/v1/models</c> to pick a model; the manually configured
/// URL/model are the fallback when nothing is detected, and the whole answer when auto-detect is off.
/// </summary>
internal interface ILocalLlmEndpointResolver
{
    Task<LocalLlmEndpoint> ResolveAsync(VoiceSettings settings, CancellationToken cancellationToken = default);
}
