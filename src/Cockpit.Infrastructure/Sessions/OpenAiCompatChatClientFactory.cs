using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// <see cref="IChatClientFactory"/> that targets a local server's OpenAI-compatible <c>/v1</c> endpoint
/// via the OpenAI SDK with a custom <see cref="OpenAIClientOptions.Endpoint"/>. Ollama ignores the API
/// key entirely; LM Studio needs one only behind a key-protected proxy — so a placeholder is sent when
/// none is configured, which both accept.
/// </summary>
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

        return CreateForEndpoint(baseUrl, model, apiKey);
    }

    public IChatClient CreateForEndpoint(string baseUrl, string model, string? apiKey = null)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1") };
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey);
        return new OpenAIClient(credential, options).GetChatClient(model).AsIChatClient();
    }
}
