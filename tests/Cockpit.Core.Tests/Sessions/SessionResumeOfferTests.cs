using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

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

        session.CanOfferResume.Should().BeTrue();
        session.ResumePrompt.Should().Be("continue", "the provider's own default fills the field");
    }

    [Fact]
    public void AnAllowanceMerelyPastItsWarningThreshold_CarriesNoOffer()
    {
        // 96% warns — keep an eye on this — but the session can still work, so there is nothing to pick up from
        // and nothing to schedule. The offer waits for the allowance to actually be gone.
        var (session, _) = Build();

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 96, DateTimeOffset.Now.AddHours(6))]);

        session.HasUsageWarning.Should().BeTrue();
        session.CanOfferResume.Should().BeFalse();
    }

    [Fact]
    public void TheOfferedMoment_IsAMinutePastTheReset_NotOnIt()
    {
        // The rollover is the provider's moment, not ours; a prompt landing on the same second can still meet a
        // spent allowance if their clock and ours disagree even slightly.
        var (session, _) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);

        session.ResumeAt.Should().Be(returns.AddMinutes(1));
    }

    [Fact]
    public void AFillingContextWindow_CarriesNoOffer()
    {
        // It empties on a compaction, not at a moment, so there is nothing to schedule against however full it is.
        var (session, _) = Build();

        session.ApplyUsage([Context], [new PluginUsageReading("context", 100, null)]);

        session.HasUsageWarning.Should().BeTrue();
        session.CanOfferResume.Should().BeFalse();
    }

    [Fact]
    public void AnAllowanceWhoseProviderDoesNotOfferResume_CarriesNoOffer()
    {
        var (session, _) = Build();
        var declared = new PluginUsageSignal(Weekly.Key, Weekly.Label, Weekly.Kind, Weekly.DefaultThresholdPercent);

        session.ApplyUsage([declared], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        session.CanOfferResume.Should().BeFalse();
    }

    [Fact]
    public async Task Scheduling_TakesTheAllowancesOwnMoment_AndSaysItIsWaiting()
    {
        var (session, store) = Build();
        var returns = DateTimeOffset.Now.AddHours(6);
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, returns)]);

        await session.ScheduleResumeCommand.ExecuteAsync(null);

        store.Saved.Should().ContainSingle();
        store.Saved[0].DueAt.Should().Be(returns.AddMinutes(1));
        store.Saved[0].Prompt.Should().Be("continue");
        session.HasPendingResume.Should().BeTrue();
        session.CanOfferResume.Should().BeFalse("one is already waiting");
        session.HasUsageWarning.Should().BeFalse("the warning has been acted on");
    }

    [Fact]
    public async Task AnEditedPrompt_IsWhatGetsScheduled()
    {
        var (session, store) = Build();
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        session.ResumePrompt = "pick up the migration where you left it";
        await session.ScheduleResumeCommand.ExecuteAsync(null);

        store.Saved[0].Prompt.Should().Be("pick up the migration where you left it");
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

        store.Saved.Should().ContainSingle();
        store.Saved[0].DueAt.Should().Be(chosen);
        store.Saved[0].Prompt.Should().Be("start with the review");
    }

    [Fact]
    public async Task BackingOutOfTheOverride_SchedulesNothing()
    {
        var (session, store) = Build();
        session.AskForResumeMoment = (_, _) => Task.FromResult<(DateTimeOffset, string)?>(null);
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        await session.ChangeResumeMomentCommand.ExecuteAsync(null);

        store.Saved.Should().BeEmpty();
        session.HasPendingResume.Should().BeFalse();
        session.CanOfferResume.Should().BeTrue("the offer is still there to take");
    }

    [Fact]
    public void WithNoWayToAsk_TheOverrideIsNotOffered()
    {
        var (session, _) = Build();

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        session.CanOfferResume.Should().BeTrue();
        session.CanChangeResumeMoment.Should().BeFalse("nothing was handed in to ask with");
    }

    [Fact]
    public async Task Cancelling_ClearsBothTheLineAndTheStorage()
    {
        var (session, store) = Build();
        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);
        await session.ScheduleResumeCommand.ExecuteAsync(null);

        await session.CancelResumeCommand.ExecuteAsync(null);

        session.HasPendingResume.Should().BeFalse();
        store.Saved.Should().BeEmpty();
    }

    [Fact]
    public void WithNoScheduler_NothingIsOffered()
    {
        // The design-time and unit-test graphs have none; the offer must simply not appear rather than throw.
        var session = new TtyViewModel();

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 100, DateTimeOffset.Now.AddHours(6))]);

        session.CanOfferResume.Should().BeFalse();
    }
}
