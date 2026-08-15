using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: registers "opencode (ACP)" as a session provider, same shape as KimiProviderPlugin. Unlike the
// OpenAiCompat providers (AC-806, AC-724), SupportsTools/SupportsPermissions are both true — a second real
// agent next to Claude, gated through Cockpit's own consent card, not a chat-only window.
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
            // The host builds the adapter from registration.Capabilities, not the driver's own property —
            // SupportsLiveModelSwitch must be declared here too. Same trap KimiProviderPlugin warns about.
            Capabilities: new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { SupportsEnvVars = true, SupportsLiveModelSwitch = true },
            CreateConfigView: existingConfigJson => new OpencodeProviderConfigView(existingConfigJson, host)));
    }

    public void Dispose()
    {
    }
}
