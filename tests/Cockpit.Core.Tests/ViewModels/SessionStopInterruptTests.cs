using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-943 — the host-side half of the interrupt fix: <see cref="SessionViewModel.StopCommand"/> must sweep a
/// pending permission row the same way <see cref="SessionViewModel.ClearContextAsync"/> already does, driver
/// agnostic. Modelled on <c>SessionClearContextTests</c>.
/// </summary>
public class SessionStopInterruptTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    [Fact]
    public async Task Stop_DuringAToolCallAwaitingPermission_ClearsTheRow()
    {
        var (vm, driver) = await StartedVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{\"command\":\"ls\"}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{\"command\":\"ls\"}" });
        Assert.True(vm.HasPendingPermission);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingPermission);
        Assert.Equal("Cancelled — interrupted", vm.Transcript.Single(entry => entry.ToolUseId == "t1").PermissionDecision);
        await driver.Received(1).InterruptAsync(Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Stop_WithNoPendingPermission_DoesNotTouchTheTranscript()
    {
        var (vm, _) = await StartedVm();

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Empty(vm.Transcript);

        await vm.DisposeAsync();
    }

    // AC-1031: the CLI reports an interrupted turn exactly like a real driver failure
    // (is_error: true, no Result) — Stop is what tells the ViewModel the difference, so the row must read
    // "Interrupted.", not a failure card asking to Retry.
    [Fact]
    public async Task TurnCompleted_AfterStop_RendersAsInterruptedNotAFailedTurn()
    {
        var (vm, _) = await StartedVm();

        await vm.StopCommand.ExecuteAsync(null);
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error_during_execution", Result = null, IsError = true });

        var row = Assert.Single(vm.Transcript);
        Assert.Equal("Interrupted.", row.Text);
        Assert.False(row.IsFailedTurnRow);
        Assert.False(row.HasAction);

        await vm.DisposeAsync();
    }

    // A failed turn NOT preceded by Stop must keep rendering as a genuine failure (AC-728/AC-939)
    // — the interrupted-row path must not swallow real errors.
    [Fact]
    public async Task TurnCompleted_ErrorWithoutStop_StillRendersAsAFailedTurn()
    {
        var (vm, _) = await StartedVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error_during_execution", Result = null, IsError = true });

        var row = Assert.Single(vm.Transcript);
        Assert.NotEqual("Interrupted.", row.Text);
        Assert.True(row.IsFailedTurnRow);

        await vm.DisposeAsync();
    }

    // AC-1031: a Stop whose own TurnCompleted never arrives (the turn already finished before the interrupt
    // landed, or the session died) must not leave the flag standing for the NEXT turn to inherit — that next
    // turn's own genuine failure would otherwise render as "Interrupted." too. Cleared at dispatch closes it.
    [Fact]
    public async Task TurnCompleted_ErrorAfterAStopThatNeverGotItsOwnTurnCompleted_StillRendersAsAFailedTurn()
    {
        var (vm, _) = await StartedVm();

        await vm.StopCommand.ExecuteAsync(null);
        // No TurnCompleted follows the Stop — simulates the interrupt landing too late, or the CLI dying.

        vm.InputText = "try again";
        await vm.SendCommand.ExecuteAsync(null);
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error_during_execution", Result = null, IsError = true });

        var row = Assert.Single(vm.Transcript, entry => entry.Kind == TranscriptEntryKind.TurnCompleted);
        Assert.NotEqual("Interrupted.", row.Text);
        Assert.True(row.IsFailedTurnRow);

        await vm.DisposeAsync();
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Driver)> StartedVm()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(driver)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort);
        return (vm, driver);
    }

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    private static ISessionDriverFactory FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
