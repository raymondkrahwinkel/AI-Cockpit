using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// A turn started from anywhere but the composer — a scheduled resume (AC-234), a wake (AC-395) — is still a turn in
/// flight, and the pane has to say so.
/// <para>
/// Worth its own tests because nothing else notices when it does not. The session keeps working, the answer still
/// arrives; what breaks is everything that asks "is this pane busy" while it happens. The composer's own guard
/// (<c>if (IsBusy)</c> before dispatching) sends on top of the running turn instead of queueing behind it, and the
/// wake gate reads a pane that is mid-turn as standing still and starts a second one.
/// </para>
/// </summary>
public class SessionViewModelPromptTurnTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    [Fact]
    public async Task SendPrompt_MarksTheTurnInFlight()
    {
        var (vm, _, _) = await _Started();

        Assert.True(await vm.SendPromptAsync("continue where you left off"));

        Assert.True(vm.IsBusy);
        Assert.Equal(SessionStatus.Busy, vm.SessionStatus);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SendPrompt_LeavesThePaneIdleAgainOnceTheTurnCompletes()
    {
        var (vm, _, _) = await _Started();
        _ = await vm.SendPromptAsync("continue where you left off");

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "ok", IsError = false });

        // The other half of the flag. Left standing it would be worse than never setting it: the pane would read as
        // permanently working, the composer would queue every later message forever, and nothing could wake it again.
        Assert.False(vm.IsBusy);
        Assert.Equal(SessionStatus.Done, vm.SessionStatus);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SendPrompt_WhoseTurnNeverLeaves_DoesNotStrandThePaneAsBusy()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_NoEvents());
        driver.SendUserMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("the provider went away")));
        var vm = new SessionViewModel(new SessionManager(_FactoryFor(driver)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        await Assert.ThrowsAsync<IOException>(() => vm.SendPromptAsync("continue where you left off"));

        Assert.False(vm.IsBusy);
        Assert.NotEqual(SessionStatus.Busy, vm.SessionStatus);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SendPrompt_OnAPaneWhoseRuntimeNeverRan_RefusesInsteadOfMarkingATurnNothingWillFinish()
    {
        // The pane keeps a runtime after a failed start, and that runtime accepts a send and does nothing with it.
        // Marking a turn in flight there is worse than the false success it replaces: with no driver there is no
        // event pump, and the two things that clear the flag both arrive on it — so the pane would read as working
        // for the rest of its life, queueing every later message behind a turn that never existed.
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(_ => throw new InvalidOperationException("no such provider"));
        var vm = new SessionViewModel(new SessionManager(factory));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.False(await vm.SendPromptAsync("continue where you left off"));

        Assert.False(vm.IsBusy);
        Assert.NotEqual(SessionStatus.Busy, vm.SessionStatus);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SendPrompt_MakesTheComposerQueueBehindItRatherThanSendOnTopOfIt()
    {
        var (vm, _, sent) = await _Started();

        _ = await vm.SendPromptAsync("continue where you left off");
        vm.InputText = "and also run the tests";
        await vm.SendCommand.ExecuteAsync(null);

        // The observable consequence, rather than the flag again: the operator's message is held as a queued chip and
        // the runtime has seen only the prompt. Without the flag the CLI is handed a second message mid-turn, which
        // its own send path says it rejects.
        Assert.Single(sent);
        Assert.Single(vm.QueuedMessages);

        await vm.DisposeAsync();
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Driver, List<string> Sent)> _Started()
    {
        var sent = new List<string>();
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_NoEvents());
        driver
            .When(session => session.SendUserMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>?>(), Arg.Any<CancellationToken>()))
            .Do(call => sent.Add(call.Arg<string>()));

        var vm = new SessionViewModel(new SessionManager(_FactoryFor(driver)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        return (vm, driver, sent);
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }

    private static async IAsyncEnumerable<SessionEvent> _NoEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Open until the runtime cancels it: a live driver's stream ends only when its process does (AC-693).
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
