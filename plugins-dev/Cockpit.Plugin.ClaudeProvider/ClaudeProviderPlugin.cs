using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Claude as a provider plugin (Fase 4): the plan overturns the 2026-07-13 "Claude wordt geen provider-plugin"
/// decision now the contract has grown enough to carry it. Registers both of Claude's routes under the id
/// <c>claude</c> (matching the host's existing Claude id, so the resolver prefers this plugin while the in-tree route
/// stays as a fallback during the transition): the <em>TTY</em> route (the interactive TUI in a pane) and the
/// <em>SDK/session-driver</em> route (headless stream-json), whose permissions ride the control protocol rather than an
/// HTTP MCP server (<see cref="ClaudeSdkSessionDriver"/>) — weg A, the plugin owns its own machinery.
/// </summary>
public sealed class ClaudeProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "claude-provider",
        DisplayName: "Claude (bundled)",
        Version: "0.3.1",
        Author: "Cockpit",
        Description: "Claude as a provider plugin. Runs the real interactive Claude TUI in a pane (TTY), with the "
            + "cockpit's workspace-trust, shared MCP servers, usage limits and the operator's own statusline preserved. "
            + "Requires the claude CLI installed and logged in on the machine running Cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services: the TTY provider is minted per session from the container.
    }

    public void Initialize(ICockpitHost host)
    {
        // Sweep the statusline snapshots of sessions that were killed rather than closed (the plugin-side
        // equivalent of the host's former startup housekeeping, now that the statusline lives here).
        ClaudeStatusLine.SweepStale();

        // The cockpit can install and manage the claude binary itself (AC-20). Registering the descriptor lets the
        // host resolve a managed copy; the providers below prefer it over PATH via host.ResolveManagedCliPath.
        host.AddManagedCli(ClaudeManagedCli.Descriptor);

        host.AddTtyProvider(new TtyProviderRegistration(
            ProviderId: ClaudeProviderIds.Claude,
            DisplayName: "Claude",
            CreateProvider: _ => new ClaudeTtyProvider(host.ResolveManagedCliPath),
            Options:
            [
                new PluginTtyLaunchOption(ClaudeTtyProvider.PermissionModeKey, "Permission mode", ClaudeOptionChoices.PermissionModes, "default")
                    { ChoiceLabels = ClaudeOptionChoices.PermissionModeLabels },
                new PluginTtyLaunchOption(ClaudeTtyProvider.ModelKey, "Model", ClaudeOptionChoices.ModelSuggestions)
                    { ChoiceLabels = ClaudeOptionChoices.ModelLabels },
                new PluginTtyLaunchOption(ClaudeTtyProvider.EffortKey, "Effort", ClaudeOptionChoices.EffortLevels)
                    { ChoiceLabels = ClaudeOptionChoices.EffortLabels },
            ])
        {
            // The provider-specific behaviours the host used to hold in-tree, now owned here (weg A) and reached
            // through the generic registration seams: read-aloud/status tail this plugin's own JSONL transcript,
            // the login gate checks its own .credentials.json, and self-detection finds its own config dirs — so
            // the core carries no Claude-format knowledge and Codex can fill the same seams for its own routes.
            CreateTranscriptReader = _ => new ClaudeTranscriptReader(),
            IsLoggedIn = ClaudeProfileDiscovery.IsLoggedIn,
            DetectProfiles = ClaudeProfileDiscovery.Detect,
        });

        // The SDK/session-driver route (weg A): the headless stream-json driver, whose tool-approval prompts ride the
        // control protocol in-band (no HTTP MCP permission server) — hence SupportsPermissions: true. Same provider id
        // as the TTY route above: a profile names "claude" and gets whichever route its session opens.
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: ClaudeProviderIds.Claude,
            DisplayName: "Claude",
            CreateDriverFactory: _ => new ClaudeSdkSessionDriverFactory(host.ResolveManagedCliPath),
            Capabilities: new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { SupportsEnvVars = true },
            CreateConfigView: existingConfigJson => new ClaudeProviderConfigView(existingConfigJson, host))
        {
            Options =
            [
                new PluginSessionLaunchOption(ClaudeSdkSessionDriver.PermissionModeOptionKey, "Permission mode", ClaudeOptionChoices.PermissionModes, "default")
                    { ChoiceLabels = ClaudeOptionChoices.PermissionModeLabels },
                new PluginSessionLaunchOption(ClaudeSdkSessionDriver.ModelOptionKey, "Model", ClaudeOptionChoices.ModelSuggestions)
                    { ChoiceLabels = ClaudeOptionChoices.ModelLabels },
                new PluginSessionLaunchOption(ClaudeSdkSessionDriver.EffortOptionKey, "Effort", ClaudeOptionChoices.EffortLevels, "medium")
                    { ChoiceLabels = ClaudeOptionChoices.EffortLabels },
            ],
        });
    }

    public void Dispose()
    {
    }
}
