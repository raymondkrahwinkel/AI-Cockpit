using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-728: a failed TurnCompleted's row had no way back except retyping the prompt — the row's already-wired
/// <c>ActionLabel</c>/<c>ActionCommand</c> mechanism (AC-715) went unused for it. Retry has to match the
/// existing Login-row pattern (AC-713/AC-720) — same severity card, same button — not a variant of its own.
/// </summary>
public class SessionViewModelFailedTurnRetryTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    [Fact]
    public void FailedTurn_RendersThroughTheSameSeverityCardAsADriverError_NotAsPlainText()
    {
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error", Result = "boom", IsError = true });

        var row = Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.TurnCompleted);
        Assert.True(row.IsFailedTurnRow);
        Assert.True(row.ShowsFailureCard);
        Assert.False(row.IsPlainNonMarkdown, "the card branch renders it, not the plain-text branch");
        // Never a guessed red/amber (AC-720's own acceptance criterion) — a failed turn carries no
        // SessionErrorKind at all, so it always falls to the same safe default an unclassified driver error gets.
        Assert.True(row.IsInformationalError);
        Assert.False(row.IsBlockingError);
        Assert.False(row.IsTemporaryError);
    }

    [Fact]
    public void SuccessfulTurnCompletedRow_NeverShowsTheFailureCard()
    {
        // AC-720's own "Signing in again…" TurnCompleted row is exactly this case — a status line, not a
        // failure — and must keep reading as plain text, not gain the card because it shares a Kind.
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.TurnCompleted, "Signing in again…");

        Assert.False(entry.IsFailedTurnRow);
        Assert.False(entry.ShowsFailureCard);
        Assert.True(entry.IsPlainNonMarkdown);
    }

    [Fact]
    public void FailedTurn_WithNoPriorDispatch_HasNoRetryAction()
    {
        // Mirrors the AC-410 resume path (SessionViewModelTests.RestoredPane_FirstTurnFailsWithNoResult...):
        // its first turn never went through _DispatchMessageAsync, so there is nothing for Retry to resend.
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error", Result = "boom", IsError = true });

        var row = Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.TurnCompleted);
        Assert.False(row.HasAction);
    }

    [Fact]
    public async Task FailedTurn_AfterADispatchedPrompt_OffersRetry()
    {
        var (vm, _, _) = await Started();
        vm.InputText = "fix the layout bug";
        await vm.SendCommand.ExecuteAsync(null);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error", Result = "boom", IsError = true });

        var row = Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.TurnCompleted);
        // Same button the "Login" row uses (AC-713): ActionLabel/ActionCommand/HasAction, nothing bespoke.
        Assert.True(row.HasAction);
        Assert.Equal("Retry", row.ActionLabel);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Retry_ResendsTheLastFailedUserTurn_WithoutRetyping()
    {
        var (vm, driver, sent) = await Started();
        vm.InputText = "fix the layout bug";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(["fix the layout bug"], sent);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error", Result = "boom", IsError = true });
        var row = Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.TurnCompleted);

        row.ActionCommand!.Execute(null);
        // The command fires the resend fire-and-forget (matching the rest of this view model's own commands);
        // give the awaited continuation a turn to run before asserting on it.
        await Task.Yield();

        Assert.Equal(["fix the layout bug", "fix the layout bug"], sent);
        await driver.Received(2).SendUserMessageAsync("fix the layout bug", Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    private static SessionViewModel NewVm()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        return new SessionViewModel(new SessionManager(FactoryFor(session)));
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Driver, List<string> Sent)> Started()
    {
        var sent = new List<string>();
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(EmptyEvents());
        driver
            .When(d => d.SendUserMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>()))
            .Do(call => sent.Add(call.Arg<string>()));

        var vm = new SessionViewModel(new SessionManager(FactoryFor(driver)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        return (vm, driver, sent);
    }

    private static ISessionDriverFactory FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
