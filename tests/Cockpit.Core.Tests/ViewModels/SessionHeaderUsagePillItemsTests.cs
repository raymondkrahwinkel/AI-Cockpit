using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
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
    public void Context_Selected_WithAFigure_RendersACtxPill()
    {
        var vm = new SessionViewModel { ContextUsedPercent = 42, UsagePillVisibleFields = [UsagePillField.Context] };

        Assert.Equal("ctx 42%", Assert.Single(vm.UsagePillItems).DisplayText);
    }

    [Fact]
    public void ASelectedMetricWithNoData_YieldsNoPill()
    {
        var vm = new SessionViewModel { ContextUsedPercent = null, UsagePillVisibleFields = [UsagePillField.Context] };
        vm.RateLimits.Clear();

        Assert.Empty(vm.UsagePillItems);
    }

    [Fact]
    public void AWindowField_RendersFromTheMatchingWindow()
    {
        var vm = new SessionViewModel { ContextUsedPercent = null };
        vm.RateLimits.Clear();
        vm.RateLimits.Add(new SessionRateWindow("5h", 64, null));
        vm.UsagePillVisibleFields = [UsagePillField.FiveHourWindow];

        Assert.Equal("5h 64%", Assert.Single(vm.UsagePillItems).DisplayText);
    }

    /// <summary>
    /// With no declared threshold — a figure that reached the header without a provider saying when it matters —
    /// the pill falls back to <see cref="UsageSeverity.FallbackThreshold"/>: amber from there, red halfway on.
    /// </summary>
    [Theory]
    [InlineData(95, "CockpitStatusErrorBrush")]
    [InlineData(88, "CockpitStatusWaitingBrush")]
    [InlineData(30, "CockpitTextSecondaryBrush")]
    public void ACtxPill_WithNoDeclaredThreshold_FallsBackToTheHostDefault(double percent, string expectedKey)
    {
        var vm = new SessionViewModel { ContextUsedPercent = percent, UsagePillVisibleFields = [UsagePillField.Context] };

        Assert.Equal(expectedKey, Assert.Single(vm.UsagePillItems).SeverityBrushKey);
    }

    /// <summary>
    /// Once a provider has declared one, the pill colours at the point that provider chose (AC-229/AC-232) — the
    /// same number the warning speaks at. Claude calls a context window worth mentioning at half full, so 70%
    /// is amber there where the host's own fallback would still call it unremarkable.
    /// </summary>
    [Fact]
    public void ACtxPill_ColoursAtTheThresholdItsProviderDeclared()
    {
        var vm = new SessionViewModel { UsagePillVisibleFields = [UsagePillField.Context] };
        var context = new PluginUsageSignal("context", "ctx", PluginUsageSignalKind.Fill, DefaultThresholdPercent: 50);

        vm.ApplyUsage([context], [new PluginUsageReading("context", 70, null)]);

        Assert.Equal("CockpitStatusWaitingBrush", Assert.Single(vm.UsagePillItems).SeverityBrushKey);
    }

    [Fact]
    public void SessionUsage_ShowsTheSummaryWithoutASeverityColour()
    {
        var vm = new SessionViewModel
        {
            HasUsage = true,
            UsageSummary = "45.2k tok · $0.01",
            UsagePillVisibleFields = [UsagePillField.SessionUsage],
        };

        var pill = Assert.Single(vm.UsagePillItems);
        Assert.Equal("45.2k tok · $0.01", pill.DisplayText);
        Assert.Equal("CockpitTextSecondaryBrush", pill.SeverityBrushKey);
    }

    [Fact]
    public void SessionUsagePill_FollowsTheLatestTooltip_EvenWhenItUpdatesAfterTheSummary()
    {
        var vm = new SessionViewModel
        {
            HasUsage = true,
            UsageSummary = "1.0k tok",
            UsagePillVisibleFields = [UsagePillField.SessionUsage],
        };

        // The usage feed sets the summary before the tooltip (SessionViewModel._AccumulateUsage order); the pill's
        // hover must reflect the tooltip's later assignment, not lag a turn behind.
        vm.UsageTooltip = "Input 900 · Output 100 · 1 turn";

        Assert.Equal("Input 900 · Output 100 · 1 turn", Assert.Single(vm.UsagePillItems).Tooltip);
    }

    [Fact]
    public void SelectingSessionUsage_HidesTheStandaloneTokenMeter()
    {
        var vm = new SessionViewModel { HasUsage = true, UsageSummary = "1.0k tok" };
        Assert.True(vm.ShowTokenMeter, "the standalone meter shows session usage by default");

        vm.UsagePillVisibleFields = [UsagePillField.SessionUsage];

        Assert.False(vm.ShowTokenMeter, "session usage now shows as a pill, so the meter yields to avoid a duplicate badge");
    }

    [Fact]
    public void TheMiniPills_FollowTheChosenOrder()
    {
        var vm = new SessionViewModel { ContextUsedPercent = 20 };
        vm.RateLimits.Clear();
        vm.RateLimits.Add(new SessionRateWindow("wk", 80, null));
        vm.UsagePillVisibleFields = [UsagePillField.WeeklyWindow, UsagePillField.Context];

        Assert.Equal(2, System.Linq.Enumerable.Count(vm.UsagePillItems));
        Assert.Equal("wk 80%", vm.UsagePillItems[0].DisplayText);
        Assert.Equal("ctx 20%", vm.UsagePillItems[1].DisplayText);
    }
}
