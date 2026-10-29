using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

// `IChatClientFactory` that targets a local server's OpenAI-compatible `/v1` endpoint via the OpenAI SDK
// with a custom `OpenAIClientOptions.Endpoint`. Ollama ignores the API key entirely; LM Studio needs one only
// behind a key-protected proxy — so a placeholder is sent when none is configured, which both accept.
internal sealed class OpenAiCompatChatClientFactory : IChatClientFactory, ISingletonService
{
    public IChatClient Create(ProviderConfig config)
    {
        var (baseUrl, model, apiKey) = config switch
        {
            OllamaConfig ollama => (ollama.BaseUrl, ollama.Model, (string?)null),
            LmStudioConfig lmStudio => (lmStudio.BaseUrl, lmStudio.Model, lmStudio.ApiKey),
            _ => throw new NotSupportedException($"Provider {config.Provider} is not OpenAI-compatible."),
        };

        return ApplyProviderRequestPolicy(config, CreateForEndpoint(baseUrl, model, apiKey));
    }

    public IChatClient CreateForEndpoint(string baseUrl, string model, string? apiKey = null)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1") };
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey);
        return new OpenAIClient(credential, options).GetChatClient(model).AsIChatClient();
    }

    // Ollama's OpenAI-compatible /v1 route supports tools, but not its native options.num_ctx. Keep the request
    // unmodified; LocalModelContextManager explains the two server-side context settings at session start.
    internal static IChatClient ApplyProviderRequestPolicy(ProviderConfig _, IChatClient client) => client;
}
