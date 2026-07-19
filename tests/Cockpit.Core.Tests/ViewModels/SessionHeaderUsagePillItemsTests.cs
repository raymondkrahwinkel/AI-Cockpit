using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.Core.UsagePill;
using FluentAssertions;

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

        vm.UsagePillItems.Should().ContainSingle().Which.DisplayText.Should().Be("ctx 42%");
    }

    [Fact]
    public void ASelectedMetricWithNoData_YieldsNoPill()
    {
        var vm = new SessionViewModel { ContextUsedPercent = null, UsagePillVisibleFields = [UsagePillField.Context] };
        vm.RateLimits.Clear();

        vm.UsagePillItems.Should().BeEmpty();
    }

    [Fact]
    public void AWindowField_RendersFromTheMatchingWindow()
    {
        var vm = new SessionViewModel { ContextUsedPercent = null };
        vm.RateLimits.Clear();
        vm.RateLimits.Add(new SessionRateWindow("5h", 64, null));
        vm.UsagePillVisibleFields = [UsagePillField.FiveHourWindow];

        vm.UsagePillItems.Should().ContainSingle().Which.DisplayText.Should().Be("5h 64%");
    }

    [Theory]
    [InlineData(90, "CockpitStatusErrorBrush")]
    [InlineData(70, "CockpitStatusWaitingBrush")]
    [InlineData(30, "CockpitTextSecondaryBrush")]
    public void ACtxPill_TakesItsSeverityColourFromThePercent(double percent, string expectedKey)
    {
        var vm = new SessionViewModel { ContextUsedPercent = percent, UsagePillVisibleFields = [UsagePillField.Context] };

        vm.UsagePillItems.Should().ContainSingle().Which.SeverityBrushKey.Should().Be(expectedKey);
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

        var pill = vm.UsagePillItems.Should().ContainSingle().Which;
        pill.DisplayText.Should().Be("45.2k tok · $0.01");
        pill.SeverityBrushKey.Should().Be("CockpitTextSecondaryBrush");
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

        vm.UsagePillItems.Should().ContainSingle().Which.Tooltip.Should().Be("Input 900 · Output 100 · 1 turn");
    }

    [Fact]
    public void SelectingSessionUsage_HidesTheStandaloneTokenMeter()
    {
        var vm = new SessionViewModel { HasUsage = true, UsageSummary = "1.0k tok" };
        vm.ShowTokenMeter.Should().BeTrue("the standalone meter shows session usage by default");

        vm.UsagePillVisibleFields = [UsagePillField.SessionUsage];

        vm.ShowTokenMeter.Should().BeFalse("session usage now shows as a pill, so the meter yields to avoid a duplicate badge");
    }

    [Fact]
    public void TheMiniPills_FollowTheChosenOrder()
    {
        var vm = new SessionViewModel { ContextUsedPercent = 20 };
        vm.RateLimits.Clear();
        vm.RateLimits.Add(new SessionRateWindow("wk", 80, null));
        vm.UsagePillVisibleFields = [UsagePillField.WeeklyWindow, UsagePillField.Context];

        vm.UsagePillItems.Should().HaveCount(2);
        vm.UsagePillItems[0].DisplayText.Should().Be("wk 80%");
        vm.UsagePillItems[1].DisplayText.Should().Be("ctx 20%");
    }
}
