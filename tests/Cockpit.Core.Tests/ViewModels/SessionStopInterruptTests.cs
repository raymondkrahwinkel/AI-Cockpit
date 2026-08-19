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
