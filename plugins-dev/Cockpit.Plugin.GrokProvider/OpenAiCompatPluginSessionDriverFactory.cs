using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;

namespace Cockpit.Plugin.GrokProvider;

// AC-724: builds an `IChatClient` against xAI's base URL via the OpenAI SDK — the same construction the
// sibling OpenAiCompat provider plugins use, on xAI's legacy chat-completions surface.
internal sealed class OpenAiCompatPluginSessionDriverFactory : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, OpenAiCompatConfig.JsonOptions)
            ?? throw new InvalidOperationException("The Grok provider config JSON did not deserialize.");

        var options = new OpenAIClientOptions { Endpoint = new Uri(config.BaseUrl) };
        var credential = new ApiKeyCredential(config.ApiKey);
        var chatClient = new OpenAIClient(credential, options).GetChatClient(config.Model).AsIChatClient();
        return new OpenAiCompatPluginSessionDriver(chatClient, config.Model);
    }
}
