using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Engine;

// The fixed notification (plan §5, action B): the task's text, delivered as it is. The cockpit notification is the
// host's toast, which a headless host writes to its log; other channels arrive with A3 (AC-1544) and fail by name.
internal sealed class NotificationAction(ICockpitHost host)
{
    public Task<ActionResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        var text = task.Action.Text.Trim();
        if (text.Length == 0)
        {
            return Task.FromResult(new ActionResult(RunOutcome.NothingToDeliver));
        }

        if (task.Delivery.Count == 0)
        {
            return Task.FromResult(new ActionResult(RunOutcome.Failed, text, "No delivery channel chosen."));
        }

        if (task.Delivery.Any(target => target.Channel == DeliveryTarget.CockpitNotification))
        {
            host.ShowToast($"{task.Name}: {text}");
        }

        var unknown = task.Delivery.Select(target => target.Channel).Where(channel => channel != DeliveryTarget.CockpitNotification).Distinct().ToList();
        return Task.FromResult(unknown.Count == 0
            ? new ActionResult(RunOutcome.Succeeded, text)
            : new ActionResult(RunOutcome.Failed, text, $"No delivery channel {string.Join(", ", unknown.Select(channel => $"'{channel}'"))} in this build."));
    }
}
