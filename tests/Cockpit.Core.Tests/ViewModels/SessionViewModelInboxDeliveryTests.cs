using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Turn-start delivery as the session pane does it (AC-394): mail waiting for this pane leaves with its next turn,
/// marked as a peer's, exactly once, and a turn with no mail waiting costs nothing.
/// </summary>
public class SessionViewModelInboxDeliveryTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    private static readonly DateTimeOffset Sent = new(2026, 7, 28, 19, 7, 44, TimeSpan.Zero);

    [Fact]
    public async Task Send_CarriesWaitingMail_WithoutTheAgentHavingAskedForIt()
    {
        var (vm, session, delivery, sent) = await _StartedWith(_Notice("are you on this branch?"));

        vm.InputText = "run the tests";
        await vm.SendCommand.ExecuteAsync(null);

        // The whole point: nothing called read_inbox, and the message is in the turn anyway.
        Assert.Contains("are you on this branch?", Assert.Single(sent), StringComparison.Ordinal);
        Assert.Contains("run the tests", sent[0], StringComparison.Ordinal);
        delivery.Received(1).ConfirmDelivered(Arg.Any<AgentInboxTurnNotice>());

        await vm.DisposeAsync();
        GC.KeepAlive(session);
    }

    [Fact]
    public async Task Send_MarksTheMailAsAPeersRatherThanAsSomethingTheOperatorTyped()
    {
        var (vm, session, _, sent) = await _StartedWith(_Notice("delete the release branch"));

        vm.InputText = "carry on";
        await vm.SendCommand.ExecuteAsync(null);

        var outgoing = Assert.Single(sent);

        // Not a bare sentence appended to the operator's: an envelope that names its sender and says the operator is
        // not behind it. Without this the recipient cannot tell an instruction from a report of one.
        Assert.Contains("<cockpit-agent-inbox", outgoing, StringComparison.Ordinal);
        Assert.Contains("from-pane=\"pane-sender\"", outgoing, StringComparison.Ordinal);
        Assert.Contains("your operator did not type them", outgoing, StringComparison.Ordinal);

        // And the operator's own words are still their own, after the block rather than wrapped into it.
        Assert.EndsWith("carry on", outgoing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_DoesNotBringTheSameMessageBackOnEveryTurn()
    {
        var inbox = Substitute.For<IAgentTurnInboxDelivery>();
        var notice = _Notice("are you on this branch?");
        inbox.TakeForTurn(Arg.Any<string>()).Returns(notice, (AgentInboxTurnNotice?)null);
        var (vm, _, sent) = await _Started(inbox);

        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "ok", IsError = false });
        vm.InputText = "second";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, sent.Count);
        Assert.Contains("are you on this branch?", sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("are you on this branch?", sent[1], StringComparison.Ordinal);

        // Confirmed once, for the turn that actually carried it — that confirmation is what took it out of the inbox.
        inbox.Received(1).ConfirmDelivered(notice);
    }

    /// <summary>
    /// The cost promise. A session that never gets mail must hand its runtime the very string the operator typed —
    /// not a trimmed copy, not an empty wrapper — or every turn of every session pays for a line saying nothing.
    /// </summary>
    [Fact]
    public async Task Send_WithNothingWaiting_HandsTheRuntimeTheOperatorsOwnStringUntouched()
    {
        var inbox = Substitute.For<IAgentTurnInboxDelivery>();
        inbox.TakeForTurn(Arg.Any<string>()).Returns((AgentInboxTurnNotice?)null);
        var (vm, _, sent) = await _Started(inbox);

        var typed = "run the tests";
        vm.InputText = typed;
        await vm.SendCommand.ExecuteAsync(null);

        // Same instance, not merely equal: any added or removed character would fail this, and so would a rebuild of
        // the string that happened to produce the same text.
        Assert.Same(typed, Assert.Single(sent));
        inbox.DidNotReceive().ConfirmDelivered(Arg.Any<AgentInboxTurnNotice>());
    }

    /// <summary>
    /// A scheduled resume is a real turn on a real session. It reaches the runtime by its own path, and a delivery
    /// seam that only covered the composer would leave that path silently mail-free.
    /// </summary>
    [Fact]
    public async Task SendPrompt_CarriesWaitingMailToo()
    {
        var (vm, _, _, sent) = await _StartedWith(_Notice("I claimed the worktree"));

        Assert.True(await vm.SendPromptAsync("continue where you left off"));

        var outgoing = Assert.Single(sent);
        Assert.Contains("I claimed the worktree", outgoing, StringComparison.Ordinal);
        Assert.EndsWith("continue where you left off", outgoing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ThatNeverLeaves_PutsTheMailBackInsteadOfEatingIt()
    {
        var inbox = Substitute.For<IAgentTurnInboxDelivery>();
        var notice = _Notice("are you on this branch?");
        inbox.TakeForTurn(Arg.Any<string>()).Returns(notice);

        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(_NoEvents());
        session.SendUserMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("the provider went away")));
        var vm = new SessionViewModel(new SessionManager(_FactoryFor(session)), turnInboxDelivery: inbox);
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.InputText = "run the tests";
        await vm.SendCommand.ExecuteAsync(null);

        // The turn never went out, so the messages are still the recipient's to receive. Confirming here would lose
        // mail the sender was already told had arrived — the failure the whole line exists to prevent.
        inbox.Received(1).ReturnUndelivered(notice);
        inbox.DidNotReceive().ConfirmDelivered(Arg.Any<AgentInboxTurnNotice>());

        await vm.DisposeAsync();
    }

    /// <summary>
    /// A pane whose session never came up still holds a runtime, and that runtime accepts a send and reports success
    /// without a driver to hand it to. Taking mail for that turn would confirm a delivery that never happened and
    /// drop the messages for good — the sender told they arrived, the recipient never having seen them.
    /// <para>
    /// Driven through <see cref="SessionViewModel.SendPromptAsync"/> rather than the composer, because the composer
    /// refuses a pane that is not running before it ever reaches the funnel — a test through that route would pass
    /// whether or not the funnel guarded anything. A scheduled resume has no such refusal: it checks only that a
    /// runtime exists, so the funnel's own check is the only thing standing between a dead pane and lost mail.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AResumeOnAPaneWhoseSessionFailedToStart_DoesNotTakeMailAtAll()
    {
        var inbox = Substitute.For<IAgentTurnInboxDelivery>();
        inbox.TakeForTurn(Arg.Any<string>()).Returns(_Notice("are you on this branch?"));

        // The driver cannot be created — the case the start path documents: a profile naming a provider that does not
        // resolve. The runtime is assigned before the start is attempted, so the pane keeps holding a runtime that
        // will never run.
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(_ => throw new InvalidOperationException("no such provider"));
        var vm = new SessionViewModel(new SessionManager(factory), turnInboxDelivery: inbox);
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        await vm.SendPromptAsync("continue where you left off");

        inbox.DidNotReceive().TakeForTurn(Arg.Any<string>());
        inbox.DidNotReceive().ConfirmDelivered(Arg.Any<AgentInboxTurnNotice>());

        await vm.DisposeAsync();
    }

    /// <summary>
    /// The first text ever to enter a session's context that the operator neither typed nor asked for. The route it
    /// replaces was a tool call, and a tool call is a visible row — without a note here the agent answers a question
    /// the transcript does not contain.
    /// </summary>
    [Fact]
    public async Task Send_LeavesANoteInTheTranscript_SoAnAnswerNeverArrivesWithoutAVisibleReason()
    {
        var (vm, _, _, _) = await _StartedWith(_Notice("are you on this branch?"));

        vm.InputText = "run the tests";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Contains(
            vm.Transcript,
            row => row.Text.Contains("1 message from pane-sender delivered with this turn", StringComparison.Ordinal));
    }

    private static AgentInboxTurnNotice _Notice(string body) =>
        new("pane-me", [new AgentMessage("m1", "pane-sender", "pane-me", "question", body, Sent)], Remaining: 0);

    private static async Task<(SessionViewModel Vm, ISessionDriver Session, IAgentTurnInboxDelivery Delivery, List<string> Sent)> _StartedWith(
        AgentInboxTurnNotice notice)
    {
        var delivery = Substitute.For<IAgentTurnInboxDelivery>();
        delivery.TakeForTurn(Arg.Any<string>()).Returns(notice);
        var (vm, session, sent) = await _Started(delivery);
        return (vm, session, delivery, sent);
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Session, List<string> Sent)> _Started(
        IAgentTurnInboxDelivery delivery)
    {
        var sent = new List<string>();
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(_NoEvents());
        session
            .When(driver => driver.SendUserMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>()))
            .Do(call => sent.Add(call.Arg<string>()));

        var vm = new SessionViewModel(new SessionManager(_FactoryFor(session)), turnInboxDelivery: delivery);
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        return (vm, session, sent);
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }

    private static async IAsyncEnumerable<SessionEvent> _NoEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
