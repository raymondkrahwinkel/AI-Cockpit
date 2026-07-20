using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Autopilot (AC-94): the operator-triggered "issue → merge-ready PR" plugin. This build (AC-149) is the skeleton
/// — it contributes a full-surface "Autopilot" workspace type (its shell) and the settings foundation. The
/// trigger, the run start, the done-gates and the tracker channel arrive in later Autopilot sub-tickets.
/// </summary>
public sealed class AutopilotPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "autopilot",
        DisplayName: "Autopilot",
        // In lockstep with plugin.json's version: the manifest gates loading, this shows in the plugin list.
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Operator-triggered \"issue → merge-ready PR\" pipeline. This build is the workspace shell and settings foundation.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new AutopilotSettings(host.Storage);

        // The gear next to the plugin in the manager opens this — the global-level settings.
        host.AddSettings(() => new AutopilotSettingsControl(settings));

        // The full-surface workspace. The type id is a frozen API surface — it is persisted on every Autopilot
        // workspace, so changing it would orphan saved ones. The body is the shell for now; the run pipeline and
        // its embedded session land in later sub-tickets.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.autopilot", "Autopilot", context => new AutopilotWorkspaceBody(context, settings))
        {
            IconKind = MaterialIconKind.RobotOutline,
            Description = "Run an issue to a merge-ready PR — the pipeline, its live session and the done-gate on one surface.",
        });
    }

    public void Dispose()
    {
    }
}
