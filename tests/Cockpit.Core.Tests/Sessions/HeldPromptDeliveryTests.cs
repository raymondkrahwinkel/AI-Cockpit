using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// A session spawned by something other than the operator is handed its brief the instant the pane exists, which is
/// before the pane can hear anything: <see cref="SessionPanelViewModel.InjectAndSubmit"/> publishes to whatever input
/// surface is wired at that moment, and a pane nobody has drawn yet has none — the brief went nowhere and the caller
/// was told nothing, so the spawn tool answered <c>ok:true</c> for a session that came up empty.
/// <para>
/// <see cref="SessionPanelViewModel.SubmitPromptWhenReady"/> is the answer, and these cover the promises in its doc:
/// it waits on <see cref="SessionPanelViewModel.CanTakeAPrompt"/> (the property that already answers "would a send
/// actually reach the agent"), it says which of the two happened, and a brief it is holding goes out exactly once.
/// </para>
/// </summary>
public class HeldPromptDeliveryTests
{
    [Fact]
    public void APaneThatCannotYetTakeAPrompt_IsNotSentTheBrief_AndTheCallerIsToldSo()
    {
        var panel = new TestSessionPanel { CanTakeAPromptOverride = false };

        var wentOutNow = panel.SubmitPromptWhenReady("review the migration");

        Assert.False(wentOutNow);
        Assert.Empty(panel.Injected);
        Assert.True(panel.HasPromptWaitingToBeDelivered);
    }

    [Fact]
    public void OnceThePaneCanTakeAPrompt_TheHeldBriefIsInjectedAndSubmitted()
    {
        var panel = new TestSessionPanel { CanTakeAPromptOverride = false };
        panel.SubmitPromptWhenReady("review the migration");

        panel.BecomeReady();

        // Injected *and* submitted: a worker sitting on an unsent message is a session the operator was told had
        // started, which is the same failure one step further along.
        Assert.Equal(new[] { "review the migration", "submit" }, panel.Injected);
        Assert.False(panel.HasPromptWaitingToBeDelivered);
    }

    [Fact]
    public void APaneThatCanAlreadyTakeAPrompt_IsSentTheBriefOnTheSpot()
    {
        var panel = new TestSessionPanel { CanTakeAPromptOverride = true };

        var wentOutNow = panel.SubmitPromptWhenReady("review the migration");

        Assert.True(wentOutNow);
        Assert.Equal(new[] { "review the migration", "submit" }, panel.Injected);
        Assert.False(panel.HasPromptWaitingToBeDelivered);
    }

    /// <summary>
    /// Readiness can be re-announced — a TTY pane's sink is re-assigned on every relaunch, an SDK pane settles its
    /// ready-gate on each configured start. A brief that already went must not go a second time: the agent would
    /// answer the same instruction twice, which on a spawn brief means doing the work twice.
    /// </summary>
    [Fact]
    public void AHeldBriefIsDeliveredExactlyOnce_HoweverOftenReadinessIsAnnounced()
    {
        var panel = new TestSessionPanel { CanTakeAPromptOverride = false };
        panel.SubmitPromptWhenReady("review the migration");

        panel.BecomeReady();
        panel.BecomeReady();
        panel.BecomeReady();

        Assert.Equal(new[] { "review the migration", "submit" }, panel.Injected);
    }

    /// <summary>
    /// A spawn without a brief is an ordinary request (the operator wants the session open, and will type into it),
    /// so blank must not be held as if something were owed — the next readiness change would otherwise submit an
    /// empty turn.
    /// </summary>
    [Fact]
    public void ABlankBriefIsNotHeld()
    {
        var panel = new TestSessionPanel { CanTakeAPromptOverride = false };

        Assert.False(panel.SubmitPromptWhenReady("   "));
        Assert.False(panel.HasPromptWaitingToBeDelivered);

        panel.BecomeReady();

        Assert.Empty(panel.Injected);
    }

    /// <summary>
    /// A session whose launch failed never becomes able to take a prompt. The brief is then simply never delivered,
    /// and the flag still says so — nothing anywhere may report it as sent.
    /// </summary>
    [Fact]
    public void ABriefForASessionThatNeverComesUp_StaysUndeliveredAndSaysSo()
    {
        var panel = new TestSessionPanel { CanTakeAPromptOverride = false };
        panel.SubmitPromptWhenReady("review the migration");

        // The launch settled, and it settled as a failure: readiness is announced, and the answer is still no.
        panel.DeliverHeldPromptForTest();

        Assert.Empty(panel.Injected);
        Assert.True(panel.HasPromptWaitingToBeDelivered);
    }
}
