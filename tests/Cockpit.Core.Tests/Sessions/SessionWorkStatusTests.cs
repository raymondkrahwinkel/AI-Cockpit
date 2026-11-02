using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// Layer 2: the shared session projection derives work from a queue-owned run.
/// </summary>
public class SessionWorkStatusTests
{
    [Fact]
    public void QueuedRun_MakesADoneSessionOutstandingBackgroundWork()
    {
        SessionRunActivity[] runs =
        [
            new("run-1", "dotnet test", "light", SessionRunState.Queued, DateTimeOffset.UtcNow, "CPU busy"),
        ];

        Assert.True(SessionWorkStatus.HasOutstandingWork(hasBackgroundShells: false, runs));
        Assert.Equal(SessionStatus.WorkingBackground, SessionWorkStatus.Derive(SessionStatus.Done, runs));
    }
}
