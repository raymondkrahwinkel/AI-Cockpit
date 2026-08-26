using Cockpit.App.ViewModels;
using Cockpit.Core.UsagePill;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-549. A Claude SDK session reported <c>ctx</c> and nothing else while "5-hour window" was ticked in Options,
/// so the setting looked broken. The pill must stay empty for a window the provider never sent (AC-530 criterion
/// 5), which leaves the flyout as the place to say why.
/// <para>
/// #1105 generalized this off a fixed "5h"/"wk" vocabulary: the notice now compares <c>RateLimits</c> against
/// whatever allowance signals the session's provider declared (via <see cref="SessionPanelViewModel.ApplyUsage"/>),
/// so it works the same way for Codex's single "7d" declaration as it did for Claude's two.
/// </para>
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

    private static readonly PluginUsageSignal _Context = new("context", "ctx", PluginUsageSignalKind.Fill, DefaultThresholdPercent: 50);
    private static readonly PluginUsageSignal _FiveHour = new("five-hour", "5h", PluginUsageSignalKind.Allowance, DefaultThresholdPercent: 90);
    private static readonly PluginUsageSignal _Weekly = new("weekly", "wk", PluginUsageSignalKind.Allowance, DefaultThresholdPercent: 90);

    private static Pane _PaneShowing(params UsagePillField[] fields) => new() { UsagePillVisibleFields = fields };

    [Fact]
    public void ADeclaredWindowTheProviderNeverSent_IsNamedInTheFlyout()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.RateWindows);
        pane.ApplyUsage([_Context, _FiveHour], [new PluginUsageReading("context", 10, null)]);

        Assert.Equal("5h: no figure reported for this session.", pane.UnreportedWindowsNotice);
        Assert.DoesNotContain(pane.UsagePillItems, item => item.DisplayText.StartsWith("5h", StringComparison.Ordinal));
    }

    [Fact]
    public void OnceTheWindowArrives_TheNoticeGoesAway()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.RateWindows);
        pane.ApplyUsage([_Context, _FiveHour],
        [
            new PluginUsageReading("context", 10, null),
            new PluginUsageReading("five-hour", 42, null),
        ]);

        Assert.Equal(string.Empty, pane.UnreportedWindowsNotice);
    }

    [Fact]
    public void BothDeclaredWindowsMissing_AreNamedTogether()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.RateWindows);
        pane.ApplyUsage([_Context, _FiveHour, _Weekly], [new PluginUsageReading("context", 10, null)]);

        Assert.Equal("5h, wk: no figure reported for this session.", pane.UnreportedWindowsNotice);
    }

    [Fact]
    public void RateWindowsNotSelected_NothingIsMentioned()
    {
        // RateWindows is one all-or-nothing toggle now (#1105 A2, replacing the old per-window 5h/wk toggles):
        // an operator who never asked to see rate windows at all should not be told one is missing.
        var pane = _PaneShowing(UsagePillField.Context);
        pane.ApplyUsage([_Context, _FiveHour], [new PluginUsageReading("context", 10, null)]);

        Assert.Equal(string.Empty, pane.UnreportedWindowsNotice);
    }

    /// <summary>
    /// A signal of kind Fill (context) never counts as a "missing window" — only Allowance signals do.
    /// </summary>
    [Fact]
    public void AMissingFillSignal_IsNotMentionedAsAWindow()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.RateWindows);
        pane.ApplyUsage([_Context, _FiveHour], [new PluginUsageReading("five-hour", 42, null)]);

        Assert.Equal(string.Empty, pane.UnreportedWindowsNotice);
    }

    /// <summary>
    /// Before the first usage reading there is nothing to conclude: a session that has not reported yet is not a
    /// provider that cannot. The flyout is not reachable then either — it hangs off the pill.
    /// </summary>
    [Fact]
    public void BeforeAnyUsageArrives_ItSaysNothing()
    {
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.RateWindows);

        Assert.Equal(string.Empty, pane.UnreportedWindowsNotice);
    }
}
