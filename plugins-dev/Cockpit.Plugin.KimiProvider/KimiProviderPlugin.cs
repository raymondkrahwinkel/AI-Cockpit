using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

// AC-268 provider-plugin sub [a]: registers "Kimi Code (ACP)" as a session provider backed by
// `KimiAcpSessionDriverFactory` — a persistent `kimi acp` subprocess speaking the Agent
// Client Protocol. No TTY route: the design deliberately keeps this ACP-only (Kimi-ACP-Provider-Design-2026-07-24.md §1)
// rather than doubling the surface with a second, parallel interactive pane.
public sealed class KimiProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kimi-provider",
        DisplayName: "Kimi Code Provider (ACP)",
        Author: "Cockpit",
        Description: "Adds Kimi Code as a session provider, driven over the Agent Client Protocol (JSON-RPC 2.0 over stdio) via `kimi acp`. Requires the kimi CLI installed and authenticated on this machine (an API key, or `kimi acp --login`). Three known limitations: a failed turn is indistinguishable from a successful one on the wire, there is no quota or cost to report, and `kimi acp` cannot receive a system prompt — the session says so rather than dropping it silently.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "kimi-provider.acp",
            DisplayName: "Kimi Code (ACP)",
            CreateDriverFactory: _ => new KimiAcpSessionDriverFactory(host.ResolveManagedCliPath),
            // P1-4: SessionDriverFactory builds the PluginSessionDriverAdapter from registration.Capabilities,
            // not from the driver instance's own Capabilities property — SupportsLiveModelSwitch must be
            // declared here too, or the host's live-model-switch wiring never reaches SetLiveOptionAsync
            // despite the driver already supporting it (ClaudeProviderPlugin.cs warns of exactly this trap).
            Capabilities: new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { SupportsEnvVars = true, SupportsLiveModelSwitch = true },
            CreateConfigView: existingConfigJson => new KimiProviderConfigView(existingConfigJson, host)));
    }

    public void Dispose()
    {
    }
}
