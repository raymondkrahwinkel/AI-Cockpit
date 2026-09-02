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

    public static IEnumerable<object[]> Notices()
    {
        UsagePillField[] both = [UsagePillField.Context, UsagePillField.RateWindows];
        PluginUsageReading[] contextOnly = [new PluginUsageReading("context", 10, null)];

        // The window was declared and never sent: the flyout is where that is said.
        yield return [both, new[] { _Context, _FiveHour }, contextOnly, "5h: no figure reported for this session."];
        // Two of them, named together rather than one notice each.
        yield return [both, new[] { _Context, _FiveHour, _Weekly }, contextOnly, "5h, wk: no figure reported for this session."];
        // It arrived, so there is nothing to explain.
        yield return
        [
            both, new[] { _Context, _FiveHour },
            new[] { new PluginUsageReading("context", 10, null), new PluginUsageReading("five-hour", 42, null) },
            string.Empty,
        ];
        // RateWindows is one all-or-nothing toggle now (#1105 A2, replacing the old per-window 5h/wk toggles): an
        // operator who never asked to see rate windows at all should not be told one is missing.
        yield return [new[] { UsagePillField.Context }, new[] { _Context, _FiveHour }, contextOnly, string.Empty];
        // A signal of kind Fill (context) never counts as a "missing window" — only Allowance signals do.
        yield return
        [
            both, new[] { _Context, _FiveHour },
            new[] { new PluginUsageReading("five-hour", 42, null) },
            string.Empty,
        ];
    }

    [Theory]
    [MemberData(nameof(Notices))]
    public void TheFlyout_NamesEveryDeclaredWindowTheProviderDidNotSend(
        UsagePillField[] fields, PluginUsageSignal[] declared, PluginUsageReading[] readings, string expected)
    {
        var pane = _PaneShowing(fields);
        pane.ApplyUsage(declared, readings);

        Assert.Equal(expected, pane.UnreportedWindowsNotice);
    }

    [Fact]
    public void AnUnsentWindow_IsLeftOutOfThePillItself_NotDrawnEmpty()
    {
        // AC-530 criterion 5, and the reason the notice has to exist at all: the pill stays silent about it, so
        // without the flyout the ticked setting simply looks broken.
        var pane = _PaneShowing(UsagePillField.Context, UsagePillField.RateWindows);
        pane.ApplyUsage([_Context, _FiveHour], [new PluginUsageReading("context", 10, null)]);

        Assert.DoesNotContain(pane.UsagePillItems, item => item.DisplayText.StartsWith("5h", StringComparison.Ordinal));
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
