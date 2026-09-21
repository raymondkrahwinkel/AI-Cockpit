using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;

namespace Cockpit.Plugin.GeminiProvider;

// `IPluginSessionDriverFactory` for this plugin's Gemini/OpenAI providers (#45): deserializes
// the profile's opaque config JSON into an `OpenAiCompatConfig` and builds an
// `IChatClient` against its base URL via the OpenAI SDK with a custom
// `OpenAIClientOptions.Endpoint` — the same construction
// `Cockpit.Infrastructure.Sessions.OpenAiCompatChatClientFactory` uses for Ollama/LM Studio.
internal sealed class OpenAiCompatPluginSessionDriverFactory(ICockpitHost host) : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson)
    {
        var config = JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, OpenAiCompatConfig.JsonOptions)
            ?? throw new InvalidOperationException("The Gemini/OpenAI provider config JSON did not deserialize.");

        var logger = host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.GeminiProvider");
        var options = BuildClientOptions(config, logger);
        var credential = new ApiKeyCredential(config.ApiKey);
        var chatClient = new OpenAIClient(credential, options).GetChatClient(config.Model).AsIChatClient();
        return new OpenAiCompatPluginSessionDriver(chatClient, config.Model, options.NetworkTimeout);
    }

    // AC-1344/AC-1345: split out so a test can assert the configured (or default) TimeoutSeconds lands on
    // the pipeline option the OpenAI SDK reads. A hand-edited cockpit.json with TimeoutSeconds <= 0 falls
    // back to the default rather than handing the SDK a non-positive network timeout, and logs that it did.
    internal static OpenAIClientOptions BuildClientOptions(OpenAiCompatConfig config, ILogger? logger = null)
    {
        var timeoutSeconds = config.TimeoutSeconds ?? OpenAiCompatConfig.DefaultTimeoutSeconds;
        if (timeoutSeconds <= 0)
        {
            logger?.LogWarning(
                "TimeoutSeconds {TimeoutSeconds} in the Gemini/OpenAI provider config is not positive; falling back to the default of {DefaultTimeoutSeconds}s",
                timeoutSeconds,
                OpenAiCompatConfig.DefaultTimeoutSeconds);
            timeoutSeconds = OpenAiCompatConfig.DefaultTimeoutSeconds;
        }

        return new()
        {
            Endpoint = new Uri(config.BaseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
    }
}
