using Cockpit.App.ViewModels;
using Cockpit.Core.UsagePill;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The header's usage pill renders one mini-pill per selected metric the session actually has data for (AC-105),
/// in the operator's chosen order, each coloured by its own severity; a selected metric with nothing to show
/// yields no pill — the same silence the single ctx pill kept.
/// </summary>
public class SessionHeaderUsagePillItemsTests
{
    [Fact]
    public void AWindowPill_SurvivesAnUpdateThatOmitsIt()
    {
        // AC-761 F1: a snapshot reporting only ctx must not blank the 5h pill a fuller snapshot already showed.
        var vm = new SessionViewModel { UsagePillVisibleFields = [UsagePillField.Context, UsagePillField.RateWindows] };
        var context = new PluginUsageSignal("context", "ctx", PluginUsageSignalKind.Fill, DefaultThresholdPercent: 50);
        var fiveHour = new PluginUsageSignal("five-hour", "5h", PluginUsageSignalKind.Allowance, DefaultThresholdPercent: 90);

        vm.ApplyUsage([context, fiveHour],
        [
            new PluginUsageReading("context", 20, null),
            new PluginUsageReading("five-hour", 18, null),
        ]);
        vm.ApplyUsage([context, fiveHour], [new PluginUsageReading("context", 21, null)]);

        Assert.Equal(2, vm.UsagePillItems.Count);
        Assert.Contains(vm.UsagePillItems, item => item.DisplayText == "5h 18%");
    }
}
