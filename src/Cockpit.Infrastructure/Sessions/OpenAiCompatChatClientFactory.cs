using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

// Ollama needs its native route to apply the profile's context size (AC-1913).
internal sealed class OpenAiCompatChatClientFactory : IChatClientFactory, ISingletonService
{
    public IChatClient Create(ProviderConfig config)
    {
        if (config is OllamaConfig ollama) return new OllamaChatClient(ollama);
        var (baseUrl, model, apiKey) = config switch
        {
            LmStudioConfig lmStudio => (lmStudio.BaseUrl, lmStudio.Model, lmStudio.ApiKey),
            _ => throw new NotSupportedException($"Provider {config.Provider} is not OpenAI-compatible."),
        };

        return new LocalReasoningChatClient(CreateForEndpoint(baseUrl, model, apiKey));
    }

    public IChatClient CreateForEndpoint(string baseUrl, string model, string? apiKey = null)
    {
        var options = BuildClientOptions(baseUrl);
        // LM Studio only needs a key behind a protected proxy; the SDK still requires a placeholder otherwise.
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey);
        return new OpenAIClient(credential, options).GetChatClient(model).AsIChatClient();
    }

    internal static OpenAIClientOptions BuildClientOptions(string baseUrl) => new()
    {
        Endpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1"),
        NetworkTimeout = Timeout.InfiniteTimeSpan,
    };
}
