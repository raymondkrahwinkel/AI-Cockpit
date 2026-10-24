using NSubstitute;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Schedules.Tests;

// AC-1544, layer 2: the delivery choices are the open channels, and a run is delivered, handed to the assistant when
// its medium's plugin is missing, or Failed with the reason when that fails too; never silently nothing.
public class TaskDeliveryTests
{
    [Theory]
    [InlineData("discord", true, RunOutcome.Succeeded, null, 0, 0)]
    [InlineData("slack", false, RunOutcome.Failed, "Delivery failed: Slack: plugin not installed", 0, 1)]
    [InlineData(null, true, RunOutcome.Failed, "No delivery channel chosen.", 0, 0)]
    public async Task ARun_IsDeliveredOrHandedToTheAssistant_AndNeverSilentlyNothing(
        string? channel, bool assistantTakesIt, RunOutcome outcome, string? reason, int toasts, int handedOver)
    {
        var host = Substitute.For<ICockpitHost>();
        host.OperatorMessageChannels.Returns([new OperatorMessageChannelInfo("discord", "Discord", [new OperatorMessageTarget("dm", "Direct message to you")])]);
        host.SendOperatorMessageAsync("discord", "dm", Arg.Any<OperatorMessage>(), Arg.Any<Func<OperatorMessageAnswer, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(OperatorMessageResult.Sent());
        host.NotifyAssistantAsync(TaskDelivery.AssistantKind, "stand-up: Stand-up").Returns(assistantTakesIt);
        var task = EngineRig.NewTask("0 9 * * *") with { Delivery = channel is null ? [] : [new DeliveryTarget(channel)] };
        var delivery = new TaskDelivery(host);

        var result = await delivery.DeliverAsync(task, "Stand-up", CancellationToken.None);

        Assert.Equal((outcome, reason), (result.Outcome, result.Reason));
        Assert.Equal(["cockpit", "discord"], delivery.Choices().Select(choice => choice.Id));
        host.Received(toasts).ShowToast("stand-up: Stand-up", PluginToastSeverity.Information, null, null);
        await host.Received(handedOver).NotifyAssistantAsync(TaskDelivery.AssistantKind, "stand-up: Stand-up");
    }
}
