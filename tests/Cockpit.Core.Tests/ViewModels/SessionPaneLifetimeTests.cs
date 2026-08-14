using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// What happens to a session pane after it is closed (AC-787). Microsoft.DI holds a reference to every
/// <see cref="IAsyncDisposable"/> its container hands out until that container is disposed — for the root provider,
/// app exit — so a pane resolved straight from it was never collected: closing a session stopped its timers and
/// killed its CLI process, but left the view model and its whole transcript, base64 image bytes and all, in memory
/// for the rest of the run. The cockpit measured 1.3 GB → 3.7 GB in an afternoon of opening and closing panes.
/// </summary>
[CollectionDefinition(SessionPaneLifetimeTests.Alone, DisableParallelization = true)]
[Collection(SessionPaneLifetimeTests.Alone)]
public class SessionPaneLifetimeTests
{
    // Whether an object is collectable is answered by the whole process, not by this test — a reference parked
    // anywhere, in another test's dispatcher queue or on a thread it left blocked, is a root like any other. So
    // this one runs on its own rather than reporting the rest of the suite's state as this pane's leak.
    public const string Alone = "session-pane-lifetime";

    // The container the way Program.cs builds it, which is the only version of this that proves anything: the leak
    // was in how a pane is resolved, not in what a pane does.
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);
        services.AddSessionPanes();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AClosedSession_IsCollected_RatherThanHeldByTheContainerForTheRun()
    {
        await using var provider = BuildProvider();
        var newSession = provider.GetRequiredService<Func<SessionViewModel>>();

        var closed = OpenAndClose(newSession);

        // Collected on a retry rather than in one go: a pane that has just been closed can still be reachable
        // through work it had in flight, which is nobody's leak and passes within a beat. A pane the container
        // holds never comes free at all, so the loop leaves early and only the failure takes the full wait.
        for (var attempt = 0; attempt < 50 && closed.IsAlive; attempt++)
        {
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(closed.IsAlive, "a closed session is still rooted somewhere — its transcript stays in memory for the run");
    }

    [Fact]
    public async Task ClosingASession_LeavesTheSingletonsItBorrowedAlone()
    {
        await using var provider = BuildProvider();
        var sessionManager = provider.GetRequiredService<ISessionManager>();

        var session = provider.GetRequiredService<Func<SessionViewModel>>()();
        await session.DisposeAsync();

        // A pane resolves through a scope of its own now, and a scope that took a singleton down with it would take
        // the running sessions of every other pane with it.
        Assert.Same(sessionManager, provider.GetRequiredService<ISessionManager>());
        Assert.NotNull(provider.GetRequiredService<Func<SessionViewModel>>()());
    }

    // Its own frame, and a synchronous one: a local in the calling method can still be live on the stack there, and
    // an async frame's state machine is a heap object that holds what it had in scope.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndClose(Func<SessionViewModel> newSession)
    {
        var session = newSession();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "look at this screenshot"));
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();

        return new WeakReference(session);
    }
}
