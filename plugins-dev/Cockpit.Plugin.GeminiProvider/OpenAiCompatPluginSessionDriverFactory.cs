using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GeminiProvider;

// `IPluginSessionDriverFactory` for this plugin's Gemini/OpenAI providers (#45): deserializes
// the profile's opaque config JSON into an `OpenAiCompatConfig` and builds an
// `IChatClient` against its base URL via the OpenAI SDK with a custom
// `OpenAIClientOptions.Endpoint` — the same construction
// `Cockpit.Infrastructure.Sessions.OpenAiCompatChatClientFactory` uses for Ollama/LM Studio.
internal sealed class OpenAiCompatPluginSessionDriverFactory : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, OpenAiCompatConfig.JsonOptions)
            ?? throw new InvalidOperationException("The Gemini/OpenAI provider config JSON did not deserialize.");

        var options = new OpenAIClientOptions { Endpoint = new Uri(config.BaseUrl) };
        var credential = new ApiKeyCredential(config.ApiKey);
        var chatClient = new OpenAIClient(credential, options).GetChatClient(config.Model).AsIChatClient();
        return new OpenAiCompatPluginSessionDriver(chatClient, config.Model);
    }
}
