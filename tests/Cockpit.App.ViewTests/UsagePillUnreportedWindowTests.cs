using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.Core.UsagePill;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-549. A Claude SDK session reported <c>ctx</c> and nothing else while "5-hour window" was ticked in Options,
/// so the setting looked broken. The pill must stay empty for a window the provider never sent (AC-530 criterion
/// 5), which leaves the flyout as the place to say why.
/// <para>
/// Against the shared base rather than <see cref="SessionViewModel"/>: that class's parameterless constructor is
/// the Avalonia previewer's, and it seeds sample 5h/wk bars — a fixture built on it can never show a window as
/// missing. Both session kinds get this from the base anyway.
/// </para>
/// </summary>
public class UsagePillUnreportedWindowTests
{
    private sealed class Pane : SessionPanelViewModel
    {
        protected override Task<string?> OnScreenshotCapturedAsync(byte[] png) => Task.FromResult<string?>(null);

        public override Task<bool> FeedVerifyResultAsync(string text, byte[] png) => Task.FromResult(false);

        protected override ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

        protected override void OnVoiceTextReady(string text) { }
    }

    private static Pane _PaneShowing(params UsagePillField[] fields) => new() { UsagePillVisibleFields = fields };

    [Fact]
    public void ATickedWindowTheProviderNeverSent_IsNamedInTheFlyout()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.FiveHourWindow);
        pane.ContextUsedPercent = 10;

        Assert.Equal("5h: not reported by this provider.", pane.UnreportedWindowsNotice);
        Assert.DoesNotContain(pane.UsagePillItems, item => item.DisplayText.StartsWith("5h", StringComparison.Ordinal));
    }

    [Fact]
    public void OnceTheWindowArrives_TheNoticeGoesAway()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.FiveHourWindow);
        pane.ContextUsedPercent = 10;
        pane.RateLimits.Add(new SessionRateWindow("5h", 42, null));

        Assert.Equal(string.Empty, pane.UnreportedWindowsNotice);
    }

    [Fact]
    public void BothTickedWindowsMissing_AreNamedTogether()
    {
        var pane = _PaneShowing(UsagePillField.FiveHourWindow, UsagePillField.WeeklyWindow);
        pane.ContextUsedPercent = 10;

        Assert.Equal("5h, wk: not reported by this provider.", pane.UnreportedWindowsNotice);
    }

    [Fact]
    public void AnUntickedWindow_IsNotMentioned()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.FiveHourWindow);
        pane.ContextUsedPercent = 10;
        pane.RateLimits.Add(new SessionRateWindow("5h", 42, null));

        // "wk" is genuinely absent too, but the operator did not ask for it — saying so would be noise.
        Assert.Equal(string.Empty, pane.UnreportedWindowsNotice);
    }

    /// <summary>
    /// Before the first usage reading there is nothing to conclude: a session that has not reported yet is not a
    /// provider that cannot. The flyout is not reachable then either — it hangs off the pill.
    /// </summary>
    [Fact]
    public void BeforeAnyUsageArrives_ItSaysNothing()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.FiveHourWindow);

        Assert.Equal(string.Empty, pane.UnreportedWindowsNotice);
    }
}
