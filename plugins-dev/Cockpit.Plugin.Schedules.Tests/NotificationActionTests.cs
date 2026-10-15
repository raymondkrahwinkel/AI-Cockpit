using NSubstitute;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Schedules.Tests;

// The first action: a fixed text, delivered as a cockpit toast, and never silently nothing.
public class NotificationActionTests
{
    [Fact]
    public async Task TheCockpitChannel_ToastsTheTaskNameAndText()
    {
        var host = Substitute.For<ICockpitHost>();

        var result = await new NotificationAction(host).RunAsync(EngineRig.NewTask("0 9 * * *"), CancellationToken.None);

        Assert.Equal(new ActionResult(RunOutcome.Succeeded, "Stand-up"), result);
        host.Received(1).ShowToast("stand-up: Stand-up", PluginToastSeverity.Information, null, null);
    }

    [Theory]
    [InlineData(" ", "cockpit", RunOutcome.NothingToDeliver, null)]
    [InlineData("Stand-up", "slack", RunOutcome.Failed, "No delivery channel 'slack' in this build.")]
    [InlineData("Stand-up", null, RunOutcome.Failed, "No delivery channel chosen.")]
    public async Task WithoutTextOrAChannelItKnows_TheRunSaysSo(string text, string? channel, RunOutcome outcome, string? reason)
    {
        var task = EngineRig.NewTask("0 9 * * *") with
        {
            Action = new TaskAction(TaskActionKind.Notification, text),
            Delivery = channel is null ? [] : [new DeliveryTarget(channel)],
        };

        var result = await new NotificationAction(Substitute.For<ICockpitHost>()).RunAsync(task, CancellationToken.None);

        Assert.Equal((outcome, reason), (result.Outcome, result.Reason));
    }
}
