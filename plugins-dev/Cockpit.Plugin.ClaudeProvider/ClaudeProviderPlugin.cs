using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Claude as a provider plugin (Fase 4): the plan overturns the 2026-07-13 "Claude wordt geen provider-plugin"
/// decision now the contract has grown enough to carry it. Increment 1 registers Claude's <em>TTY</em> route as a
/// plugin (the interactive TUI in a pane); the SDK/session-driver route follows in later increments. The provider
/// id matches the host's existing Claude TTY id (<c>claude</c>) so the resolver can prefer this plugin over the
/// in-tree provider while the old route stays as a fallback during the transition.
/// </summary>
public sealed class ClaudeProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "claude-provider",
        DisplayName: "Claude (bundled)",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Claude as a provider plugin. Runs the real interactive Claude TUI in a pane (TTY), with the "
            + "cockpit's workspace-trust, shared MCP servers, usage limits and the operator's own statusline preserved. "
            + "Requires the claude CLI installed and logged in on the machine running Cockpit.");

    private static readonly IReadOnlyList<string> _PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions"];

    // The CLI's own aliases, offered as free-text suggestions (empty-vs-populated is the dialog's editable/dropdown
    // switch) so a specific model or snapshot can still be pinned — the same list the host's dialog suggests.
    private static readonly IReadOnlyList<string> _ModelSuggestions = ["opus", "sonnet", "haiku"];

    private static readonly IReadOnlyList<string> _EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services: the TTY provider is minted per session from the container.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddTtyProvider(new TtyProviderRegistration(
            ProviderId: ClaudeProviderIds.Claude,
            DisplayName: "Claude",
            CreateProvider: _ => new ClaudeTtyProvider(),
            Options:
            [
                new PluginTtyLaunchOption(ClaudeTtyProvider.PermissionModeKey, "Permission mode", _PermissionModes, "default"),
                new PluginTtyLaunchOption(ClaudeTtyProvider.ModelKey, "Model", _ModelSuggestions),
                new PluginTtyLaunchOption(ClaudeTtyProvider.EffortKey, "Effort", _EffortLevels),
            ]));
    }

    public void Dispose()
    {
    }
}
