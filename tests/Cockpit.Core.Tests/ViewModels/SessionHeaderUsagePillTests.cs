using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The shared header's usage pill shows when there is a context figure <em>or</em> any usage window (AC-37). The
/// 5h/wk windows are reachable only through the pill's flyout, so gating the pill on the context figure alone hid
/// them whenever a provider reported rate limits without a ctx figure — e.g. right after a /compact.
/// </summary>
public class SessionHeaderUsagePillTests
{
    [Fact]
    public void WithAContextFigure_ThePillShows()
    {
        // The design-time constructor seeds ctx + two windows.
        new SessionViewModel().HasUsagePill.Should().BeTrue();
    }

    [Fact]
    public void WithRateWindowsButNoContext_ThePillStillShows()
    {
        var vm = new SessionViewModel { ContextUsedPercent = null };

        vm.HasUsagePill.Should().BeTrue("the 5h/wk windows are only reachable through the pill's flyout");
    }

    [Fact]
    public void WithNeitherContextNorWindows_ThePillIsHidden()
    {
        var vm = new SessionViewModel { ContextUsedPercent = null };
        vm.RateLimits.Clear();

        vm.HasUsagePill.Should().BeFalse();
    }

    [Fact]
    public void AddingARateWindow_RaisesHasUsagePill()
    {
        var vm = new SessionViewModel { ContextUsedPercent = null };
        vm.RateLimits.Clear();
        vm.HasUsagePill.Should().BeFalse();

        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(SessionViewModel.HasUsagePill);
        vm.RateLimits.Add(new SessionRateWindow("5h", 50, null));

        raised.Should().BeTrue("HasUsagePill depends on the RateLimits collection");
        vm.HasUsagePill.Should().BeTrue();
    }
}
