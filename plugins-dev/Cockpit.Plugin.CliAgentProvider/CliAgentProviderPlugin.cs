using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// Fase B1 provider-plugin (#45): registers "Codex (CLI)" as a session provider backed by
/// <see cref="CliSubprocessPluginSessionDriverFactory"/> — a proces-per-turn subprocess driver, unlike the
/// Gemini/OpenAI provider plugin's persistent <c>IChatClient</c>. Experimental: see this project's own
/// header comment and the design doc for what fase B2 (live Codex verification) still owes.
/// </summary>
public sealed class CliAgentProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "cli-agent-provider",
        DisplayName: "CLI Agent Provider (Codex)",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Experimental: adds Codex CLI as a selectable session provider, driven as a subprocess per turn. Requires the codex CLI installed and authenticated on this machine (CODEX_API_KEY or `codex login`). No in-band tool-permission channel — the sandbox/approval mode is fixed per profile.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "cli-agent-provider.codex",
            DisplayName: "Codex (CLI)",
            CreateDriverFactory: _ => new CliSubprocessPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: false),
            CreateConfigView: existingConfigJson => new CliAgentProviderConfigView(existingConfigJson)));

        // Same provider id as the session provider above — a profile names a provider, and what that
        // provider can do (a headless driver, a TUI, or both, per PluginTtyContracts) is what it registered.
        // Codex's own words for its start defaults (see CodexTtyProvider's remarks for why these are not
        // Claude's permission-mode/effort): the sandbox policy and the model override.
        host.AddTtyProvider(new TtyProviderRegistration(
            ProviderId: "cli-agent-provider.codex",
            DisplayName: "Codex (CLI)",
            CreateProvider: _ => new CodexTtyProvider(),
            Options:
            [
                new PluginTtyLaunchOption(
                    CodexTtyProvider.SandboxOptionKey,
                    "Sandbox",
                    Choices: ["read-only", "workspace-write", "danger-full-access"]),
                new PluginTtyLaunchOption(
                    CodexTtyProvider.ModelOptionKey,
                    "Model",
                    Choices: []),
            ]));
    }

    public void Dispose()
    {
    }
}
