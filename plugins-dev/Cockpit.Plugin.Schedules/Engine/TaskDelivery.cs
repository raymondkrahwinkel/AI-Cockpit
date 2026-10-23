using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Schedules.Engine;

// Takes what a run produced to the task's channels: the fixed notification's text (plan §5, action B) or an agent's
// answer. Nothing but whitespace is nothing to deliver. A channel is whatever communication plugin is open (AC-1544);
// one that is missing or fails hands the message to the assistant, and only when that fails too is the run Failed.
internal sealed class TaskDelivery(ICockpitHost host)
{
    public const string AssistantKind = "schedule";

    // What a task can deliver to right now: the cockpit notification and every open channel with its targets.
    public IReadOnlyList<OperatorMessageChannelInfo> Choices() =>
        [new OperatorMessageChannelInfo(DeliveryTarget.CockpitNotification, "Cockpit notification", []), .. host.OperatorMessageChannels];

    public async Task<ActionResult> DeliverAsync(ScheduledTask task, string output, CancellationToken cancellationToken)
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

        var message = $"{task.Name}: {text}";
        if (task.Delivery.Any(target => target.Channel == DeliveryTarget.CockpitNotification))
        {
            host.ShowToast(message);
        }

        var (notes, failed) = await SendToChannelsAsync(task.Delivery, new OperatorMessage { Text = message }, null, cancellationToken);
        return failed is null
            ? new ActionResult(RunOutcome.Succeeded, text, notes)
            : new ActionResult(RunOutcome.Failed, text, failed);
    }

    // Every chosen channel but the cockpit notification, a task's run and a reminder alike (AC-1549): a missing plugin
    // or a failed send hands the text to the assistant. The notes say so; Failed is set only when that was refused too.
    public async Task<(string? Notes, string? Failed)> SendToChannelsAsync(
        IEnumerable<DeliveryTarget> targets, OperatorMessage message, Func<OperatorMessageAnswer, Task>? onAnswer, CancellationToken cancellationToken)
    {
        List<string> handedOver = [];
        List<string> failed = [];
        foreach (var target in targets.Distinct().Where(target => target.Channel != DeliveryTarget.CockpitNotification))
        {
            if (await _SendAsync(target, message, onAnswer, cancellationToken) is not { } problem)
            {
                continue;
            }

            if (await host.NotifyAssistantAsync(AssistantKind, message.Text))
            {
                handedOver.Add($"{problem} → handed to the assistant ✓");
            }
            else
            {
                failed.Add(problem);
            }
        }

        var notes = handedOver.Count == 0 ? null : string.Join("; ", handedOver);
        return (notes, failed.Count == 0 ? null : $"Delivery failed: {string.Join("; ", [.. failed, .. handedOver])}");
    }

    // Null when the channel took it; else what went wrong, named after the channel, as the run history shows it.
    private async Task<string?> _SendAsync(DeliveryTarget target, OperatorMessage message, Func<OperatorMessageAnswer, Task>? onAnswer, CancellationToken cancellationToken)
    {
        var channel = host.OperatorMessageChannels.FirstOrDefault(open => open.Id == target.Channel);
        var name = channel?.DisplayName ?? (target.Channel is { Length: > 0 } id ? $"{char.ToUpperInvariant(id[0])}{id[1..]}" : "A channel");
        if (channel is null)
        {
            return $"{name}: plugin not installed";
        }

        if ((target.Target ?? channel.Targets.FirstOrDefault()?.Id) is not { } targetId)
        {
            return $"{name}: nowhere to deliver to";
        }

        var result = await host.SendOperatorMessageAsync(channel.Id, targetId, message, onAnswer, cancellationToken);
        return result.Ok ? null : $"{name}: {result.Error}";
    }
}
