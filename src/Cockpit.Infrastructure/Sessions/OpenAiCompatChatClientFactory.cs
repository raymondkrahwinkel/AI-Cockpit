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

        var client = CreateForEndpoint(baseUrl, model, apiKey);
        return config is OllamaConfig ollamaConfig
            ? new OllamaContextChatClient(client, LocalModelContext.Normalize(ollamaConfig.ContextTokens))
            : client;
    }

    public IChatClient CreateForEndpoint(string baseUrl, string model, string? apiKey = null)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1") };
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey);
        return new OpenAIClient(credential, options).GetChatClient(model).AsIChatClient();
    }
}

// Ollama's OpenAI-compatible endpoint accepts its native request options alongside the OpenAI fields. Injecting
// num_ctx through the SDK's raw representation hook keeps the full Microsoft.Extensions.AI tool/streaming stack and
// applies the profile value to every request, including follow-up tool rounds.
internal sealed class OllamaContextChatClient(IChatClient inner, int contextTokens) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, _WithContext(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, _WithContext(options), cancellationToken);

    private ChatOptions _WithContext(ChatOptions? options)
    {
        var effective = options?.Clone() ?? new ChatOptions();
        var previousFactory = effective.RawRepresentationFactory;
        effective.RawRepresentationFactory = client => _Patched(previousFactory?.Invoke(client));
        return effective;
    }

    private object _Patched(object? existing)
    {
        var raw = existing as OpenAI.Chat.ChatCompletionOptions ?? new OpenAI.Chat.ChatCompletionOptions();
#pragma warning disable SCME0001 // OpenAI's supported extra-request-field hook; required for Ollama's options.num_ctx.
        raw.Patch.Set("$.options.num_ctx"u8, LocalModelContext.Normalize(contextTokens));
#pragma warning restore SCME0001
        return raw;
    }
}
