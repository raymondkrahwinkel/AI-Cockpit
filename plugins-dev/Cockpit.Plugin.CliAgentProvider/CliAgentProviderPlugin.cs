using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// Fase B1 provider-plugin (#45): registers "Codex (CLI)" as a session provider backed by
// `CliSubprocessPluginSessionDriverFactory` — a proces-per-turn subprocess driver, unlike the
// Gemini/OpenAI provider plugin's persistent `IChatClient`. Experimental — fase B2 still owes live verification.
public sealed class CliAgentProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "cli-agent-provider",
        DisplayName: "Codex (ChatGPT)",
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
        // session a profile opens. Sandbox is a fixed set; Model is free text as a fallback, upgraded to
        // the live model/list at open (ResolveOptionsAsync below), for both the SDK and TTY route.
        var sdkSandbox = new PluginSessionLaunchOption(CodexAppServerSessionDriver.SandboxOptionKey, "Sandbox", CodexSandbox.Choices, DefaultValue: "read-only");
        var sdkModelFallback = new PluginSessionLaunchOption(CodexAppServerSessionDriver.ModelOptionKey, "Model", Choices: []);

        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "cli-agent-provider.codex",
            DisplayName: "Codex (CLI)",
            // The app-server driver replaces the headless exec driver as the interactive Codex provider (#45
            // fase 3): it speaks JSON-RPC to a persistent `codex app-server`, so it supports live approvals.
            CreateDriverFactory: _ => new CodexAppServerPluginSessionDriverFactory(host.ResolveManagedCliPath),
            // ConfinesFileAccessToWorkingDirectory (AC-174) and DeclaredOptions (AC-649, Codex's own sandbox
            // vocabulary) are declared here because the host honours the registration, not the driver instance.
            Capabilities: new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true)
            {
                SupportsEnvVars = true,
                ConfinesFileAccessToWorkingDirectory = true,
                DeclaredOptions =
                [
                    new(CodexAppServerSessionDriver.SandboxOptionKey, "Sandbox", [.. CodexSandbox.Choices.Select(choice => new PluginSessionOptionValue(choice, choice))], "read-only"),
                    new(CodexAppServerSessionDriver.ModelOptionKey, "Model"),
                ],
            },
            CreateConfigView: existingConfigJson => new CliAgentProviderConfigView(existingConfigJson, host))
        {
            Options = [sdkSandbox, sdkModelFallback],
            // AC-713: Codex had no login gate at all before this — every profile read as ready regardless of
            // whether `codex login`/`CODEX_API_KEY` was actually set up. `codex login status`'s exit code is the
            // only structured signal the CLI offers (see `CodexLoginStatus`).
            IsLoggedIn = configJson => CodexLoginStatus.IsLoggedIn(configJson, host.ResolveManagedCliPath),
            StartLogin = (configJson, ct) => CodexLoginFlow.Start(configJson, host.ResolveManagedCliPath, ct),
            ResolveOptionsAsync = async (configJson, cancellationToken) =>
            {
                var listing = await _ListModelsAsync(configJson, host.ResolveManagedCliPath, cancellationToken).ConfigureAwait(false);
                var model = listing.Ids.Count == 0
                    ? sdkModelFallback
                    : new PluginSessionLaunchOption(CodexAppServerSessionDriver.ModelOptionKey, "Model", listing.Ids, listing.DefaultId);
                return [sdkSandbox, model];
            },
        });

        // Same provider id as the session provider above — a profile names a provider, and what it can do
        // (headless, TUI, or both, per PluginTtyContracts) is what it registered. Codex's own start
        // defaults (sandbox + model), with the same live model/list upgrade as the SDK route.
        var ttySandbox = new PluginTtyLaunchOption(CodexTtyProvider.SandboxOptionKey, "Sandbox", CodexSandbox.Choices);
        var ttyModelFallback = new PluginTtyLaunchOption(CodexTtyProvider.ModelOptionKey, "Model", Choices: []);

        host.AddTtyProvider(new TtyProviderRegistration(
            ProviderId: "cli-agent-provider.codex",
            DisplayName: "Codex (CLI)",
            CreateProvider: _ => new CodexTtyProvider(host.ResolveManagedCliPath),
            Options: [ttySandbox, ttyModelFallback])
        {
            // Declared on both routes (AC-629), same as Claude — an SDK-only provider would otherwise be the
            // example that leaves the gate silent.
            IsLoggedIn = configJson => CodexLoginStatus.IsLoggedIn(configJson, host.ResolveManagedCliPath),
            StartLogin = (configJson, ct) => CodexLoginFlow.Start(configJson, host.ResolveManagedCliPath, ct),
            ResolveOptionsAsync = async (configJson, cancellationToken) =>
            {
                var listing = await _ListModelsAsync(configJson, host.ResolveManagedCliPath, cancellationToken).ConfigureAwait(false);
                var model = listing.Ids.Count == 0
                    ? ttyModelFallback
                    : new PluginTtyLaunchOption(CodexTtyProvider.ModelOptionKey, "Model", listing.Ids, listing.DefaultId);
                return [ttySandbox, model];
            },
            // AC-171: without this the host has no way to tail a Codex TTY session's rollout file, so the status
            // dot is set to Idle once at launch and never moves — see CodexTranscriptReader's own remarks.
            CreateTranscriptReader = _ => new CodexTranscriptReader(),
        });
    }

    // Reads the models this profile's codex offers (increment 2 step C) — shared by the SDK and TTY option resolvers.
    private static async Task<CodexModelListing> _ListModelsAsync(string configJson, Func<string, string?>? managedResolver, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions) ?? new CliAgentConfig();
        // Resolve with the managed copy too, so a codex installed only via the managed installer (not on PATH) still
        // lists its models — otherwise the New-session Model dropdown falls back to free text even though the session
        // itself would spawn the managed binary fine.
        var executablePath = CliExecutableLocator.Resolve(config.Command, managedResolver);
        return await CodexModelCatalog.ListAsync(() => new ProcessCliSubprocess(), config, executablePath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
