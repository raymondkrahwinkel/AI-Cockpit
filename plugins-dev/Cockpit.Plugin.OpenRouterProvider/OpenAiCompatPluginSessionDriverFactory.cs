using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;

namespace Cockpit.Plugin.OpenRouterProvider;

// AC-806: builds an `IChatClient` against OpenRouter's base URL via the OpenAI SDK — the same
// construction the sibling OpenAiCompat provider plugins use for their own endpoints.
internal sealed class OpenAiCompatPluginSessionDriverFactory : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, OpenAiCompatConfig.JsonOptions)
            ?? throw new InvalidOperationException("The OpenRouter provider config JSON did not deserialize.");

        // AC-806: OpenRouter's optional attribution headers (HTTP-Referer/X-Title) only feed its public
        // leaderboard ranking, so they are left out — no effect on the request itself.
        var options = new OpenAIClientOptions { Endpoint = new Uri(config.BaseUrl) };
        var credential = new ApiKeyCredential(config.ApiKey);
        var chatClient = new OpenAIClient(credential, options).GetChatClient(config.Model).AsIChatClient();
        return new OpenAiCompatPluginSessionDriver(chatClient, config.Model);
    }
}
