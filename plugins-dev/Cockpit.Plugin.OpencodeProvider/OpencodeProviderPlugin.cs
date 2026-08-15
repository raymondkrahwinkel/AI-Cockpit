using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783 provider-plugin: registers "opencode (ACP)" as a session provider backed by
// `OpencodeAcpSessionDriverFactory` — a persistent `opencode acp` subprocess speaking the Agent Client
// Protocol, the same shape `Cockpit.Plugin.KimiProvider.KimiProviderPlugin` uses. This is the one of the
// three 2026-08-15 provider tickets (AC-806 OpenRouter, AC-724 Grok, AC-783) that lands on the ACP route
// rather than the OpenAI-compat one: `SupportsTools`/`SupportsPermissions` are both true here, so a profile
// on this provider is a second real agent next to Claude — tools run, and Cockpit's own consent card gates
// them — not a chat-only window the way the other two are.
public sealed class OpencodeProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "opencode-provider",
        DisplayName: "opencode Provider (ACP)",
        Author: "Cockpit",
        Description: "Adds opencode as a session provider, driven over the Agent Client Protocol (JSON-RPC 2.0 over stdio) via `opencode acp`. Requires the opencode CLI installed on this machine (opencode.ai/docs) and, for most models, authenticated (an API key, or `opencode auth login`) — opencode's own free-tier models need neither. Real tool calls and permission prompts, unlike the chat-only OpenAI-compatible providers: every tool call is routed through this provider's own forced \"ask\" permission policy so it always reaches Cockpit's consent card, overriding whatever the target project's own opencode.json permission config says while a Cockpit profile is driving the session. Live usage/cost figures stream per turn; opencode has no way to receive a system prompt over ACP, so a profile identity or project instruction is not applied — the session says so in the transcript rather than dropping it silently.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "opencode-provider.acp",
            DisplayName: "opencode (ACP)",
            CreateDriverFactory: _ => new OpencodeAcpSessionDriverFactory(host.ResolveManagedCliPath),
            // The host builds the session adapter from registration.Capabilities, not from the driver
            // instance's own Capabilities property — SupportsLiveModelSwitch must be declared here too, or
            // the host's live-model-switch wiring never reaches SetLiveOptionAsync despite the driver already
            // supporting it. Same trap KimiProviderPlugin's own comment warns about, verified against the
            // same host code path here rather than taken on faith.
            Capabilities: new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { SupportsEnvVars = true, SupportsLiveModelSwitch = true },
            CreateConfigView: existingConfigJson => new OpencodeProviderConfigView(existingConfigJson, host)));
    }

    public void Dispose()
    {
    }
}
