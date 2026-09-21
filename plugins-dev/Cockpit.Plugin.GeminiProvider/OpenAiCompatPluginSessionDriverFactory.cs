using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;

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

        var options = BuildClientOptions(config);
        var credential = new ApiKeyCredential(config.ApiKey);
        var chatClient = new OpenAIClient(credential, options).GetChatClient(config.Model).AsIChatClient();
        return new OpenAiCompatPluginSessionDriver(chatClient, config.Model, options.NetworkTimeout);
    }

    // AC-1344: split out so a test can assert the configured (or default) TimeoutSeconds actually lands on
    // the pipeline option the OpenAI SDK reads, rather than only on the record it was parsed into.
    internal static OpenAIClientOptions BuildClientOptions(OpenAiCompatConfig config) => new()
    {
        Endpoint = new Uri(config.BaseUrl),
        NetworkTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds ?? OpenAiCompatConfig.DefaultTimeoutSeconds),
    };
}
