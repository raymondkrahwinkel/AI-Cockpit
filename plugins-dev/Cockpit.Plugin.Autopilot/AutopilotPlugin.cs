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
        var runs = new AutopilotRunController();

        // The gear next to the plugin in the manager opens this — the global-level settings.
        host.AddSettings(() => new AutopilotSettingsControl(settings));

        // The trigger's receiving half (AC-150) plus the opstart flow (AC-151): a tracker's "Start in Autopilot" sends
        // this intent; Autopilot records the point, surfaces its workspace, runs a short scoping judgment, and either
        // parks the point (refused) or advances it to running — the body then embeds the isolated session.
        host.RegisterIntentHandler("start", async intent =>
        {
            var run = AutopilotRun.FromIntent(intent);
            runs.BeginScoping(run);
            await host.OpenWorkspaceAsync("workspace.autopilot");

            var verdict = await _ScopeAsync(host, settings, run);

            // Scoping can take a while; if a newer point took over the surface meanwhile, its own start drives it —
            // this late verdict must not flip that run.
            if (!runs.IsCurrent(run))
            {
                return new Dictionary<string, string> { ["status"] = "superseded", ["issue"] = run.IssueId };
            }

            if (!verdict.IsWorkable)
            {
                runs.Refuse(verdict.Reason);
                return new Dictionary<string, string> { ["status"] = "refused", ["issue"] = run.IssueId, ["reason"] = verdict.Reason };
            }

            // Advance to running: the workspace body embeds the isolated session, confirms the autonomous run with the
            // operator over it (AC-152), and briefs the agent only on approval.
            runs.MarkRunning();
            return new Dictionary<string, string> { ["status"] = "started", ["issue"] = run.IssueId };
        });

        // The full-surface workspace. The type id is a frozen API surface — it is persisted on every Autopilot
        // workspace, so changing it would orphan saved ones. The body reads the started run from the shared controller;
        // the run pipeline and its embedded session land in later sub-tickets.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.autopilot", "Autopilot", context => new AutopilotWorkspaceBody(host, context, settings, runs))
        {
            IconKind = MaterialIconKind.RobotOutline,
            Description = "Run an issue to a merge-ready PR — the pipeline, its live session and the done-gate on one surface.",
        });
    }

    public void Dispose()
    {
    }

    // Scoping (decision #3): delegate a short judgment to the configured profile and read its verdict. No profile set
    // → the point starts unjudged; a delegation error → it starts anyway, since the operator asked for it explicitly
    // and a broken gate must not swallow their intent.
    private static async Task<ScopingVerdict> _ScopeAsync(ICockpitHost host, AutopilotSettings settings, AutopilotRun run)
    {
        var profile = settings.ScopingProfileLabel();
        if (string.IsNullOrWhiteSpace(profile))
        {
            return ScopingVerdict.Workable;
        }

        try
        {
            var answer = await host.Actions.DelegateAsync(profile, _BuildScopingPrompt(run), timeout: TimeSpan.FromMinutes(2));
            return ScopingVerdict.Parse(answer);
        }
        catch (Exception)
        {
            return ScopingVerdict.Workable;
        }
    }

    private static string _BuildScopingPrompt(AutopilotRun run)
    {
        var description = run.Data.GetValueOrDefault("description", string.Empty);
        return $"""
            You are scoping a work item for an automated "issue to merge-ready PR" run. Decide whether it is workable
            as-is: clear enough, small enough, and with a discernible acceptance. Refuse it only when it is too large,
            too vague, or has no clear acceptance to work towards.

            Answer on the FIRST line with exactly one of:
            WORKABLE
            REFUSE: <one short sentence naming what is missing>

            Item ({run.Tracker} {run.IssueId}): {run.Title}
            {description}
            """;
    }
}
