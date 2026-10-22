using NSubstitute;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Schedules.Tests;

// The delivery: a fixed text as a cockpit toast, and never silently nothing. An empty text is in AgentPromptActionTests.
public class TaskDeliveryTests
{
    [Fact]
    public void TheCockpitChannel_ToastsTheTaskNameAndText()
    {
        var host = Substitute.For<ICockpitHost>();

        var result = new TaskDelivery(host).Deliver(EngineRig.NewTask("0 9 * * *"), "Stand-up");

        Assert.Equal(new ActionResult(RunOutcome.Succeeded, "Stand-up"), result);
        host.Received(1).ShowToast("stand-up: Stand-up", PluginToastSeverity.Information, null, null);
    }

    [Theory]
    [InlineData("slack", "No delivery channel 'slack' in this build.")]
    [InlineData(null, "No delivery channel chosen.")]
    public void WithoutAChannelItKnows_TheRunFailsAndSaysSo(string? channel, string reason)
    {
        var task = EngineRig.NewTask("0 9 * * *") with
        {
            Delivery = channel is null ? [] : [new DeliveryTarget(channel)],
        };

        var result = new TaskDelivery(Substitute.For<ICockpitHost>()).Deliver(task, "Stand-up");

        Assert.Equal((RunOutcome.Failed, reason), (result.Outcome, result.Reason));
    }
}
