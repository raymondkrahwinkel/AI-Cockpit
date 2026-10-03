using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Plugin.Workflows.Engine;

internal sealed class WorkflowRunsHealth(
    WorkflowStore store,
    RunStore runs,
    FlowWatcher watcher,
    TimeProvider time) : IPluginHealthSection, IPluginHealthActions
{
    public string Name => "workflows-runs";

    public PluginHealthReport Read()
    {
        var now = time.GetUtcNow();
        var scheduled = store.Load()
            .Where(workflow => workflow.IsActive)
            .SelectMany(workflow => workflow.Nodes
                .Where(node => node.TypeId == "cockpit.schedule" && !node.IsDisabled)
                .Take(1)
                .Select(trigger => (Workflow: workflow, Trigger: trigger)))
            .Select(entry =>
            {
                var trigger = entry.Trigger;
                var next = Schedule.ResolveZone(trigger.Parameters.GetValueOrDefault("Time zone")) is { } zone
                    ? Schedule.Next(trigger.Parameters.GetValueOrDefault("When") ?? string.Empty, zone, now)
                    : null;
                return (entry.Workflow, Trigger: trigger, Next: next);
            })
            .OrderBy(entry => entry.Next is null)
            .ThenBy(entry => entry.Next)
            .ThenBy(entry => entry.Workflow.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<PluginHealthRow>();
        var first = scheduled.FirstOrDefault(entry => entry.Next is not null);
        var summaryLabel = first.Workflow is null
            ? $"{scheduled.Count.ToString(CultureInfo.InvariantCulture)} scheduled"
            : $"{scheduled.Count.ToString(CultureInfo.InvariantCulture)} scheduled · next: {first.Workflow.Name}";
        rows.Add(new PluginHealthRow(summaryLabel, PluginHealthStatus.Ok, first.Next));

        foreach (var entry in scheduled)
        {
            var latest = watcher.Active(entry.Workflow.Id) ?? runs.For(entry.Workflow.Id).FirstOrDefault();
            rows.Add(new PluginHealthRow(
                $"{entry.Workflow.Name} · {_Outcome(latest)}",
                latest?.Status == RunStatus.Failed ? PluginHealthStatus.Failed : PluginHealthStatus.Ok,
                latest?.FinishedAt ?? latest?.StartedAt)
            {
                ActionId = _ActionId(entry.Workflow.Id),
            });
            rows.Add(new PluginHealthRow($"{entry.Workflow.Name} · Next run", PluginHealthStatus.Ok, entry.Next));
        }

        return new PluginHealthReport(true, rows);
    }

    public Task<PluginHealthActionResult> RunAsync(string actionId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new PluginHealthActionResult(false));
        }

        var workflow = store.Load().FirstOrDefault(candidate =>
            candidate.IsActive
            && candidate.Nodes.Any(node => node.TypeId == "cockpit.schedule" && !node.IsDisabled)
            && _ActionId(candidate.Id) == actionId);
        return Task.FromResult(new PluginHealthActionResult(workflow is not null && watcher.TryRunNow(workflow.Id)));
    }

    private static string _ActionId(string workflowId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(workflowId));
        return $"run:{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static string _Outcome(WorkflowRun? run)
    {
        if (run is null)
        {
            return "Not run · Never";
        }

        if (run.Phase == WorkflowRunPhase.WaitingForPermission)
        {
            return "Waiting for your permission";
        }

        if (run.Reason != WorkflowRunReason.None)
        {
            return $"Not run · {run.Reason switch
            {
                WorkflowRunReason.Disabled => "Disabled",
                WorkflowRunReason.AlreadyRunning => "Already running",
                WorkflowRunReason.Missed => "Missed",
                WorkflowRunReason.ConsentDenied => "Consent denied",
                _ => "Unknown",
            }}";
        }

        if (run.WasCaughtUp)
        {
            return "Missed, caught up";
        }

        if (run.ApprovalOrigin == WorkflowApprovalOrigin.Discord)
        {
            return "Approved in Discord";
        }

        if (run.ApprovalOrigin is WorkflowApprovalOrigin.Local or WorkflowApprovalOrigin.Unknown)
        {
            return "Approved";
        }

        if (run.Steps.Any(step => step.Status == RunStatus.Succeeded && step.TypeId is "cockpit.discord" or "discord.send-dm"))
        {
            return "Sent to Discord";
        }

        return run.Status switch
        {
            RunStatus.Running => "Running",
            RunStatus.Succeeded => $"Done · {_Duration(run.Duration)}",
            RunStatus.Failed => "Failed",
            _ => "Not run",
        };
    }

    private static string _Duration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{Math.Max(1, (int)Math.Round(duration.TotalHours)).ToString(CultureInfo.InvariantCulture)} h";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} m";
        }

        return $"{Math.Max(0, (int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} s";
    }
}
