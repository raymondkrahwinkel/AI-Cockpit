using System.Text.Json;
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
        Version: "0.1.1",
        Author: "Cockpit",
        Description: "Experimental: adds Codex CLI as a selectable session provider, driven as a subprocess per turn. Requires the codex CLI installed and authenticated on this machine (CODEX_API_KEY or `codex login`). No in-band tool-permission channel — the sandbox/approval mode is fixed per profile.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services of its own — every driver instance is minted fresh per
        // session from the profile's config JSON, so there is nothing to register here.
    }

    public void Initialize(ICockpitHost host)
    {
        // The cockpit can install and manage the codex binary itself (AC-20). Registering the descriptor lets the host
        // resolve a managed copy; the driver factory and TTY provider prefer it over PATH via host.ResolveManagedCliPath.
        host.AddManagedCli(CodexManagedCli.Descriptor);

        // The per-session start defaults the New-session dialog asks about — the same two whichever kind of
        // session a profile opens, so it means the same thing either way. Sandbox is a fixed set; Model is
        // declared as free text (the fallback) but the dialog upgrades it to the live model/list at open
        // (ResolveOptionsAsync below), for both the SDK and the TTY route.
        var sdkSandbox = new PluginSessionLaunchOption(CodexAppServerSessionDriver.SandboxOptionKey, "Sandbox", CodexSandbox.Choices, DefaultValue: "read-only");
        var sdkModelFallback = new PluginSessionLaunchOption(CodexAppServerSessionDriver.ModelOptionKey, "Model", Choices: []);

        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "cli-agent-provider.codex",
            DisplayName: "Codex (CLI)",
            // The app-server driver replaces the headless exec driver as the interactive Codex provider (#45
            // fase 3): it speaks JSON-RPC to a persistent `codex app-server`, so it supports live approvals —
            // hence SupportsPermissions: true, where the exec route reported false.
            CreateDriverFactory: _ => new CodexAppServerPluginSessionDriverFactory(host.ResolveManagedCliPath),
            Capabilities: new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { SupportsEnvVars = true },
            CreateConfigView: existingConfigJson => new CliAgentProviderConfigView(existingConfigJson, host))
        {
            Options = [sdkSandbox, sdkModelFallback],
            ResolveOptionsAsync = async (configJson, cancellationToken) =>
            {
                var listing = await _ListModelsAsync(configJson, cancellationToken).ConfigureAwait(false);
                var model = listing.Ids.Count == 0
                    ? sdkModelFallback
                    : new PluginSessionLaunchOption(CodexAppServerSessionDriver.ModelOptionKey, "Model", listing.Ids, listing.DefaultId);
                return [sdkSandbox, model];
            },
        });

        // Same provider id as the session provider above — a profile names a provider, and what that provider can
        // do (a headless driver, a TUI, or both, per PluginTtyContracts) is what it registered. Codex's own words
        // for its start defaults (see CodexTtyProvider's remarks for why these are not Claude's permission-mode/
        // effort): the sandbox policy and the model override — same live model/list upgrade as the SDK route.
        var ttySandbox = new PluginTtyLaunchOption(CodexTtyProvider.SandboxOptionKey, "Sandbox", CodexSandbox.Choices);
        var ttyModelFallback = new PluginTtyLaunchOption(CodexTtyProvider.ModelOptionKey, "Model", Choices: []);

        host.AddTtyProvider(new TtyProviderRegistration(
            ProviderId: "cli-agent-provider.codex",
            DisplayName: "Codex (CLI)",
            CreateProvider: _ => new CodexTtyProvider(host.ResolveManagedCliPath),
            Options: [ttySandbox, ttyModelFallback])
        {
            ResolveOptionsAsync = async (configJson, cancellationToken) =>
            {
                var listing = await _ListModelsAsync(configJson, cancellationToken).ConfigureAwait(false);
                var model = listing.Ids.Count == 0
                    ? ttyModelFallback
                    : new PluginTtyLaunchOption(CodexTtyProvider.ModelOptionKey, "Model", listing.Ids, listing.DefaultId);
                return [ttySandbox, model];
            },
        });
    }

    /// <summary>Reads the models this profile's codex offers (increment 2 step C) — shared by the SDK and TTY option resolvers.</summary>
    private static async Task<CodexModelListing> _ListModelsAsync(string configJson, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions) ?? new CliAgentConfig();
        var executablePath = CliExecutableLocator.Resolve(config.Command);
        return await CodexModelCatalog.ListAsync(() => new ProcessCliSubprocess(), config, executablePath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
