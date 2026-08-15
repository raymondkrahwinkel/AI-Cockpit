using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpenRouterProvider;

// `IPluginSessionDriverFactory` for this plugin's OpenRouter provider (#45/#63/AC-806): deserializes
// the profile's opaque config JSON into an `OpenAiCompatConfig` and builds an
// `IChatClient` against its base URL via the OpenAI SDK with a custom
// `OpenAIClientOptions.Endpoint` — the same construction
// `Cockpit.Infrastructure.Sessions.OpenAiCompatChatClientFactory` uses for Ollama/LM Studio, and the
// Gemini/OpenAI and GitHub Models provider plugins use for their own providers.
internal sealed class OpenAiCompatPluginSessionDriverFactory : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, OpenAiCompatConfig.JsonOptions)
            ?? throw new InvalidOperationException("The OpenRouter provider config JSON did not deserialize.");

        // AC-806 criterion 7: OpenRouter's docs offer two optional attribution headers (`HTTP-Referer`,
        // `X-Title`) that only feed its public leaderboard ranking — they carry no effect on the request
        // itself and OpenRouter's own quickstart calls them optional. Left out on purpose: sending them would
        // need a per-plugin "app name/URL" concept this host has no other use for, to buy a ranking listing
        // Cockpit has no stake in.
        var options = new OpenAIClientOptions { Endpoint = new Uri(config.BaseUrl) };
        var credential = new ApiKeyCredential(config.ApiKey);
        var chatClient = new OpenAIClient(credential, options).GetChatClient(config.Model).AsIChatClient();
        return new OpenAiCompatPluginSessionDriver(chatClient, config.Model);
    }
}
