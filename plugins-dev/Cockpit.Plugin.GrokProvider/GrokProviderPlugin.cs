using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GrokProvider;

// Provider-plugin (AC-724): registers "Grok" as a selectable session provider, backed by the same
// `OpenAiCompatPluginSessionDriverFactory` the Gemini/OpenAI (#45), GitHub Models (#63) and OpenRouter
// (AC-806) provider plugins use — it differs only in which OpenAI-compatible base URL a profile targets
// (api.x.ai/v1). Chat-only capabilities (no tools/permissions/live model switch/plan mode/thinking) — see
// `OpenAiCompatPluginSessionDriver.Capabilities`. Declares no usage signals, same reason as OpenRouter
// (AC-806): the chat-completions response carries no rolling allowance/context figure this driver reads.
//
// AC-724 criterion 7 — legacy chat-completions, not the Responses API: xAI's docs (checked 2026-08-15) list
// the Responses API as the path they are building toward and now call chat-completions "Legacy", but the
// legacy endpoint is what this driver's `IChatClient` construction (the OpenAI SDK's `GetChatClient`)
// speaks — the same call every other OpenAiCompat provider plugin in this tree makes. Moving to Responses
// would mean a driver that is not shared with Gemini/GitHub Models/OpenRouter, for a path that works
// identically today; deliberately deferred rather than an oversight.
//
// AC-724 criterion 1 — grok also speaks an agent protocol, out of scope here: `grok --help` documents
// `--output-format streaming-json` as "NDJSON of the agent native ACP session updates", and a dedicated
// `grok agent stdio` subcommand ("Run the agent over stdio") exists — the same shape
// `Cockpit.Plugin.KimiProvider` already drives over `kimi acp`. Measured live: an unauthenticated `grok
// agent stdio` does not answer JSON-RPC on stdin directly — it opens an interactive OAuth device-code flow
// first (drawn to stderr), which this session had no xAI/SuperGrok credential to complete, so the actual
// `initialize` handshake was not exercised end to end. The finding itself (yes, a controllable protocol
// exists) is written up for Raymond to decide whether a route-B ticket (a real second agent, like AC-783)
// is worth opening; this ticket only builds route A.
public sealed class GrokProviderPlugin : ICockpitPlugin
{
    // xAI's OpenAI-compatible endpoint (docs.x.ai/docs/api-reference).
    internal const string GrokDefaultBaseUrl = "https://api.x.ai/v1";

    public PluginMetadata Metadata { get; } = new(
        Id: "grok-provider",
        DisplayName: "Grok",
        Author: "Cockpit",
        Description: "Experimental: adds Grok (xAI) as a selectable session provider, over its OpenAI-compatible legacy chat-completions endpoint via Microsoft.Extensions.AI. Chat-only — no tools, file access or permission prompts. Configure an xAI API key and model (e.g. grok-4.6) per profile in Manage profiles.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "grok-provider.grok",
            DisplayName: "Grok",
            CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
            CreateConfigView: existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, GrokDefaultBaseUrl),
            DefaultBaseUrl: GrokDefaultBaseUrl));
    }

    public void Dispose()
    {
    }
}
