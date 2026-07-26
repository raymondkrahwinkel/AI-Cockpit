using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// AC-268 provider-plugin sub [a]: registers "Kimi Code (ACP)" as a session provider backed by
/// <see cref="KimiAcpSessionDriverFactory"/> — a persistent <c>kimi acp</c> subprocess speaking the Agent
/// Client Protocol. No TTY route: the design deliberately keeps this ACP-only (Kimi-ACP-Provider-Design-2026-07-24.md §1)
/// rather than doubling the surface with a second, parallel interactive pane.
/// </summary>
public sealed class KimiProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kimi-provider",
        DisplayName: "Kimi Code Provider (ACP)",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Adds Kimi Code as a session provider, driven over the Agent Client Protocol (JSON-RPC 2.0 over stdio) via `kimi acp`. Requires the kimi CLI installed and authenticated on this machine (an API key, or `kimi acp --login`). Known limitation: Kimi maps a failed turn to the same stopReason as a successful one, so this provider cannot tell the two apart from the wire alone.");

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
