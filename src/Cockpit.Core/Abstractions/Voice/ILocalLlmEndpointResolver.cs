using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Decides which local OpenAI-compatible server (Ollama/LM Studio) and model the shared voice-LLM call uses.
/// With auto-detect on it reuses the process-table detection behind the memory breakdown (<c>LocalModelServers</c>,
/// #78) to find the running server, then reads its <c>/v1/models</c> to pick a model; the manually configured
/// URL/model are the fallback when nothing is detected, and the whole answer when auto-detect is off.
/// </summary>
public interface ILocalLlmEndpointResolver
{
    Task<LocalLlmEndpoint> ResolveAsync(VoiceSettings settings, CancellationToken cancellationToken = default);
}
