using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The offer a warning carries when a session can be picked up again (AC-231): only for an allowance that says
/// when it returns, only where its provider offered it, and only until one is actually waiting.
/// </summary>
public class SessionResumeOfferTests
{
    private sealed class InMemoryStore : IScheduledResumeStore
    {
        public List<ScheduledResume> Saved { get; set; } = [];

        public Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledResume>>(Saved);

        public Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default)
        {
            Saved = [.. resumes];
            return Task.CompletedTask;
        }
    }

    private static readonly PluginUsageSignal Weekly =
        new("weekly", "wk", PluginUsageSignalKind.Allowance, 90)
        {
            Description = "Week",
            SupportsResume = true,
            DefaultResumePrompt = "continue",
        };

    private static readonly PluginUsageSignal FiveHour =
        new("five-hour", "5h", PluginUsageSignalKind.Allowance, 90)
        {
            Description = "Session (5 hours)",
            SupportsResume = true,
            DefaultResumePrompt = "carry on",
        };

    private static readonly PluginUsageSignal Context =
        new("context", "ctx", PluginUsageSignalKind.Fill, 50) { Description = "Context window" };

    private static (TtyViewModel Session, InMemoryStore Store) Build()
    {
        var store = new InMemoryStore();
        var session = new TtyViewModel { Resumes = new ScheduledResumeCoordinator(store) };

        return (session, store);
    }

    [Fact]
    public void AnAllowanceThatIsActuallySpent_CarriesTheOffer()
    {
        var (session, _) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);

        Assert.True(session.CanOfferResume);
        Assert.Equal("continue", session.ResumePrompt);
    }

    [Fact]
    public void AnAllowanceMerelyPastItsWarningThreshold_CarriesNoOffer()
    {
        // 96% warns — keep an eye on this — but the session can still work, so there is nothing to pick up from
        // and nothing to schedule. The offer waits for the allowance to actually be gone.
        var (session, _) = Build();

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 96, DateTimeOffset.Now.AddHours(6))]);

        Assert.True(session.HasUsageWarning);
        Assert.False(session.CanOfferResume);
    }

    [Fact]
    public void TheOfferedMoment_IsAMinutePastTheReset_NotOnIt()
    {
        // The rollover is the provider's moment, not ours; a prompt landing on the same second can still meet a
        // spent allowance if their clock and ours disagree even slightly.
        var (session, _) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);

        Assert.Equal(returns.AddMinutes(1), session.ResumeAt);
    }

    [Fact]
    public void AFillingContextWindow_CarriesNoOffer()
    {
        // It empties on a compaction, not at a moment, so there is nothing to schedule against however full it is.
        var (session, _) = Build();

        session.ApplyUsage([Context], [new PluginUsageReading("context", 100, null)]);

        Assert.True(session.HasUsageWarning);
        Assert.False(session.CanOfferResume);
    }

    [Fact]
    public void AnAllowanceWhoseProviderDoesNotOfferResume_CarriesNoOffer()
    {
        var (session, _) = Build();
        var declared = new PluginUsageSignal(Weekly.Key, Weekly.Label, Weekly.Kind, Weekly.DefaultThresholdPercent);

        session.ApplyUsage([declared], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        Assert.False(session.CanOfferResume);
    }

    [Fact]
    public async Task Scheduling_TakesTheAllowancesOwnMoment_AndSaysItIsWaiting()
    {
        var (session, store) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);

        await session.ScheduleResumeCommand.ExecuteAsync(null);

        Assert.Single(store.Saved);
        Assert.Equal(returns.AddMinutes(1), store.Saved[0].DueAt);
        Assert.Equal("continue", store.Saved[0].Prompt);
        Assert.True(session.HasPendingResume);
        Assert.False(session.CanOfferResume, "one is already waiting");
        Assert.False(session.HasUsageWarning, "the warning has been acted on");
    }

    [Fact]
    public async Task AnEditedPrompt_IsWhatGetsScheduled()
    {
        var (session, store) = Build();
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        session.ResumePrompt = "pick up the migration where you left it";
        await session.ScheduleResumeCommand.ExecuteAsync(null);

        Assert.Equal("pick up the migration where you left it", store.Saved[0].Prompt);
    }

    [Fact]
    public async Task AnOverriddenMoment_IsWhatGetsScheduled()
    {
        // The reset is the sensible default, not a rule: a week that returns at 11:00 on a Saturday is no use to
        // someone who will not be back until Monday.
        var (session, store) = Build();
        var chosen = DateTimeOffset.Now.AddDays(2);
        session.AskForResumeMoment = (_, _) => Task.FromResult<(DateTimeOffset, string)?>((chosen, "start with the review"));
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        await session.ChangeResumeMomentCommand.ExecuteAsync(null);

        Assert.Single(store.Saved);
        Assert.Equal(chosen, store.Saved[0].DueAt);
        Assert.Equal("start with the review", store.Saved[0].Prompt);
    }

    [Fact]
    public async Task BackingOutOfTheOverride_SchedulesNothing()
    {
        var (session, store) = Build();
        session.AskForResumeMoment = (_, _) => Task.FromResult<(DateTimeOffset, string)?>(null);
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        await session.ChangeResumeMomentCommand.ExecuteAsync(null);

        Assert.Empty(store.Saved);
        Assert.False(session.HasPendingResume);
        Assert.True(session.CanOfferResume, "the offer is still there to take");
    }

    [Fact]
    public void WithNoWayToAsk_TheOverrideIsNotOffered()
    {
        var (session, _) = Build();

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        Assert.True(session.CanOfferResume);
        Assert.False(session.CanChangeResumeMoment, "nothing was handed in to ask with");
    }

    [Fact]
    public async Task Cancelling_ClearsBothTheLineAndTheStorage()
    {
        var (session, store) = Build();
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);
        await session.ScheduleResumeCommand.ExecuteAsync(null);

        await session.CancelResumeCommand.ExecuteAsync(null);

        Assert.False(session.HasPendingResume);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task APromptWrittenOverSeveralLines_ArrivesAsOneInstruction()
    {
        // A terminal reads any line break as "send now". Left alone, a two-line prompt would submit its first
        // line and type the rest into whatever the session did next — the instruction arrives cut in half and
        // something acts on the half.
        var sent = new List<string>();
        var session = new TtyViewModel { PromptSink = sent.Add };

        await session.SendPromptAsync("pick up the migration\nstart with the schema");

        Assert.Equal("pick up the migration start with the schema\r", Assert.Single(sent));
    }

    [Fact]
    public async Task WithNoTerminalBehindIt_TheSessionSaysItCouldNotTakeThePrompt()
    {
        // The caller reports a resume that could not be delivered rather than assuming it landed.
        var session = new TtyViewModel();

        Assert.False(await session.SendPromptAsync("continue"));
    }

    [Fact]
    public void WithNoScheduler_NothingIsOffered()
    {
        // The design-time and unit-test graphs have none; the offer must simply not appear rather than throw.
        var session = new TtyViewModel();

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        Assert.False(session.CanOfferResume);
    }

    [Fact]
    public void AnAllowanceThatClimbsToSpentAfterWarning_StillOffersTheResume()
    {
        // How an allowance actually reaches 100%: gradually, having warned somewhere on the way up. Measured only
        // on the reading that crossed the warning line, the offer appeared for a signal whose very first reading
        // past its line already read 100% — which is to say, in practice, for nobody at all.
        var (session, _) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 95, returns)]);
        Assert.False(session.CanOfferResume, "96% still leaves a session that can work");

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);

        Assert.True(session.CanOfferResume, "the week is spent now, however gradually it got there");
        Assert.Equal(returns.AddMinutes(1), session.ResumeAt);
    }

    [Fact]
    public async Task APromptBeingTypedIntoTheBox_SurvivesTheNextPoll()
    {
        // The offer is made once per standing period rather than on every reading over 100%, because the polls
        // keep coming while the box is open — rewriting the prompt each time would take the words out from under
        // whoever is typing them.
        var (session, store) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);

        session.ResumePrompt = "pick up the migration where you left it";
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);
        await session.ScheduleResumeCommand.ExecuteAsync(null);

        Assert.Equal("pick up the migration where you left it", store.Saved[0].Prompt);
    }

    [Fact]
    public void AnOfferArrivingAfterTheBarWasDismissed_ComesWithABarToReachItIn()
    {
        // Clicking away "Week is 91% used" dismisses a thing worth watching. What arrives later is a different
        // message — the week is gone, and here is a button about it — and that button lives inside the bar. Left
        // silenced, the offer exists with nothing on screen to act on it through.
        var (session, _) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 91, returns)]);
        session.DismissUsageWarningCommand.Execute(null);

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);

        Assert.True(session.CanOfferResume);
        Assert.True(session.HasUsageWarning, "an offer behind a hidden bar is not an offer");
        Assert.Contains("Week is 100% used", session.UsageWarning);
    }

    [Fact]
    public async Task TwoSpentAllowances_DoNotTradeTheOfferBackAndForth()
    {
        // There is one prompt box and one moment on the bar. With both a session and a week fully spent, each poll
        // used to hand the offer from one to the other and rewrite the box with the other's default — so anything
        // typed into it was gone by the next reading, without a keystroke from anyone.
        var (session, store) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);
        PluginUsageReading[] bothSpent =
        [
            new("five-hour", 100, returns),
            new("weekly", 100, returns),
        ];
        session.ApplyUsage([FiveHour, Weekly], bothSpent);

        session.ResumePrompt = "my carefully typed instruction";
        session.ApplyUsage([FiveHour, Weekly], bothSpent);

        Assert.Equal("my carefully typed instruction", session.ResumePrompt);

        await session.ScheduleResumeCommand.ExecuteAsync(null);
        Assert.Equal("my carefully typed instruction", store.Saved[0].Prompt);
    }

    [Fact]
    public void AnAllowanceThatRollsOver_TakesItsOfferWithIt()
    {
        // A week back at 5% has nothing to be picked up from later, so the button to schedule that pick-up goes
        // with the warning that justified it.
        var (session, _) = Build();
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);
        Assert.True(session.CanOfferResume);

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 5, DateTimeOffset.Now.AddDays(7))]);

        Assert.False(session.HasUsageWarning);
        Assert.False(session.CanOfferResume);
        Assert.Null(session.ResumeAt);
    }

    [Fact]
    public async Task AResumeAlreadyWaiting_SurvivesItsAllowanceRollingOver()
    {
        // The offer is ours to withdraw; a moment the operator has committed to is theirs to cancel. Dropping it
        // because the figure behind it recovered would silently break a promise the cockpit made.
        var (session, store) = Build();
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);
        await session.ScheduleResumeCommand.ExecuteAsync(null);

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 5, DateTimeOffset.Now.AddDays(7))]);

        Assert.True(session.HasPendingResume);
        Assert.Single(store.Saved);
    }

    [Fact]
    public void AnOfferStandingUnderSomeoneElsesWarning_StaysOnScreenAndTakeable()
    {
        // The warning is one string shared by every signal, so a later crossing overwrites the words while the
        // earlier signal's offer is still standing. Two things have to survive that: the offer must not be
        // withdrawn because the context bar went quiet, and it must still be reachable — the buttons live inside
        // the banner, and the banner is only on screen while there are words in it. Asserting CanOfferResume
        // alone would pass with the offer sitting behind a hidden banner, which is not an offer at all.
        var (session, _) = Build();
        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);
        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("context", 60, null)]);

        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("context", 4, null)]);

        Assert.True(session.CanOfferResume, "the week is still spent, whatever the context bar is doing");
        Assert.True(session.HasUsageWarning, "the banner carrying the offer's buttons must still be shown");
        Assert.Contains("Week is 100% used", session.UsageWarning);
    }

    [Fact]
    public async Task AnOfferAlreadyActedOn_DoesNotGetItsWordsBack()
    {
        // The banner is handed back to a standing offer because the offer's buttons live in it. Once a resume is
        // waiting there are no buttons left to reach, so there is nothing to keep on screen — and the old sentence
        // would put a decision the operator already made back up with only a Dismiss underneath it.
        var (session, _) = Build();
        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);
        await session.ScheduleResumeCommand.ExecuteAsync(null);
        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("context", 60, null)]);

        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("context", 4, null)]);

        Assert.False(session.HasUsageWarning, "the weekly offer has already been acted on");
        Assert.True(session.HasPendingResume, "and the resume it was acted on into is still waiting");
    }

    [Fact]
    public void ABannerClickedAway_DoesNotComeBackOnAnotherSignalsAccount()
    {
        // Handing the banner back to a standing offer is right when its words were overwritten, and wrong when
        // the operator dismissed it — that was a decision about the whole bar. Dismiss forgets which signal is
        // shown, which is what keeps the two cases apart.
        var (session, _) = Build();
        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);
        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("context", 60, null)]);
        session.DismissUsageWarningCommand.Execute(null);

        session.ApplyUsage([Weekly, Context], [new PluginUsageReading("context", 4, null)]);

        Assert.False(session.HasUsageWarning, "it was clicked away on purpose");
    }
}
