using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The session register (#68): the one place a live session is created, found and stopped — so an interactive
/// panel closing and an orchestrator's stop_task (#67) end a session the same way, and "what is running" has a
/// single answer.
/// </summary>
public class SessionManagerTests
{
    [Fact]
    public void Create_RegistersTheSession_AndAnnouncesTheChange()
    {
        var manager = new SessionManager(_Factory());
        var changes = 0;
        manager.SessionsChanged += () => changes++;

        var runtime = manager.Create(profile: null);

        var only = Assert.Single(manager.Sessions);
        Assert.Equal(runtime, only);
        Assert.Equal(runtime, manager.Find(runtime.Id));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task StopAsync_RemovesTheSession_SoItIsNoLongerFindable()
    {
        var manager = new SessionManager(_Factory());
        var runtime = manager.Create(profile: null);

        await manager.StopAsync(runtime.Id);

        Assert.Empty(manager.Sessions);
        Assert.Null(manager.Find(runtime.Id));
    }

    [Fact]
    public async Task StopAsync_OnAnAlreadyStoppedSession_DoesNotThrow()
    {
        // A panel closing and a stop_task can race for the same session; the loser must be a no-op rather than
        // an exception on a shutdown path.
        var manager = new SessionManager(_Factory());
        var runtime = manager.Create(profile: null);
        await manager.StopAsync(runtime.Id);

        await manager.StopAsync(runtime.Id);
    }

    [Fact]
    public async Task StopAsync_OnAnUnknownId_DoesNotThrow()
    {
        var manager = new SessionManager(_Factory());

        await manager.StopAsync("no-such-session");
    }

    private static ISessionDriverFactory _Factory()
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ISessionDriver>());
        return factory;
    }
}
