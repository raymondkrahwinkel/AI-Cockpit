using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Engine;

// Takes what a run produced to the task's channels: the fixed notification's text (plan §5, action B) or an agent's
// answer. Nothing but whitespace is nothing to deliver. The cockpit notification is the host's toast, which a headless
// host writes to its log; other channels arrive with A3 (AC-1544) and fail by name.
internal sealed class TaskDelivery(ICockpitHost host)
{
    public ActionResult Deliver(ScheduledTask task, string output)
    {
        var text = output.Trim();
        if (text.Length == 0)
        {
            return new ActionResult(RunOutcome.NothingToDeliver);
        }

        if (task.Delivery.Count == 0)
        {
            return new ActionResult(RunOutcome.Failed, text, "No delivery channel chosen.");
        }

        if (task.Delivery.Any(target => target.Channel == DeliveryTarget.CockpitNotification))
        {
            host.ShowToast($"{task.Name}: {text}");
        }

        var unknown = task.Delivery.Select(target => target.Channel).Where(channel => channel != DeliveryTarget.CockpitNotification).Distinct().ToList();
        return unknown.Count == 0
            ? new ActionResult(RunOutcome.Succeeded, text)
            : new ActionResult(RunOutcome.Failed, text, $"No delivery channel {string.Join(", ", unknown.Select(channel => $"'{channel}'"))} in this build.");
    }
}
