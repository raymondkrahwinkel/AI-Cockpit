using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// Two cockpits over one state directory write over each other's settings, and the second one's startup deletes
/// the mcp-config and plugin files the first one's sessions are still reading (AC-4). These tests hold the claim
/// that stops the second one — and the exemption that lets a development build run beside it anyway (AC-3).
/// </summary>
/// <remarks>
/// <para>
/// The claim is held on a thread of its own, because a mutex is owned by a thread and is re-entrant to it: asking
/// for it twice from the test's own thread is granted both times, which measures nothing. A separate owner is the
/// nearest thing to the second cockpit this suite can produce in one process.
/// </para>
/// <para>
/// What that leaves unproven is the reach of the claim across processes and — on Unix — across shells, which is
/// the .NET/OS guarantee the options below buy rather than anything this code decides. It was measured by hand on
/// Windows (two processes: taken, refused, and no stale claim after a hard kill); on Fedora it is still the open
/// question in <c>Memory/Cockpit/Todo.md</c>.
/// </para>
/// <para>
/// Every test claims a name of its own. The real name is system-wide by design, so a test using it would answer
/// to whether a cockpit happens to be open on this machine: red on Raymond's desktop while he is using the app,
/// green on a runner, for reasons that have nothing to do with the code.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuardTests
{
    private static string UniqueClaimName() => $"AI-Cockpit-test-{Guid.NewGuid():N}";

    [Fact]
    public void TryAcquire_WhenNothingHoldsTheClaim_TakesIt()
    {
        using var guard = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, UniqueClaimName());

        guard.Should().NotBeNull();
    }

    [Fact]
    public void TryAcquire_WhileAnotherCockpitHoldsTheClaim_Refuses()
    {
        var claimName = UniqueClaimName();
        using var other = new CockpitHoldingTheClaim(claimName);

        var second = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName);

        second.Should().BeNull("a second cockpit must find the claim taken and stand down");
    }

    [Fact]
    public void TryAcquire_AfterTheHolderReleasedTheClaim_TakesItAgain()
    {
        var claimName = UniqueClaimName();
        new CockpitHoldingTheClaim(claimName).Dispose();

        using var next = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName);

        next.Should().NotBeNull("a closed cockpit must not keep the next one out");
    }

    [Fact]
    public void TryAcquire_WithAWait_WhileAnotherCockpitStillHoldsTheClaim_StillRefusesOnceItTimesOut()
    {
        var claimName = UniqueClaimName();
        using var other = new CockpitHoldingTheClaim(claimName);

        var second = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName, TimeSpan.FromMilliseconds(200));

        second.Should().BeNull("the claim never came free, so even with a wait the second cockpit must stand down");
    }

    [Fact]
    public void TryAcquire_WithAWait_WhenTheHolderReleasesDuringIt_WinsTheHandoff()
    {
        // The restart race: the new cockpit starts while the old one still holds the claim, and takes it once the
        // old one lets go. With the zero wait the other tests use this returns null instead — which is the bug the
        // wait fixes (the "already running" notice after "Restart now").
        var claimName = UniqueClaimName();
        var other = new CockpitHoldingTheClaim(claimName);

        // Acquired and released on the one thread, because a mutex is owned by the thread that took it — the same
        // reason the holder above lives on a thread of its own.
        var handoff = Task.Run(() =>
        {
            using var next = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName, TimeSpan.FromSeconds(5));
            return next is not null;
        });

        // Let the waiter reach its WaitOne with the claim still held, so this exercises acquiring on release and
        // not merely acquiring a claim that was already free.
        Thread.Sleep(300);
        other.Dispose();

        handoff.GetAwaiter().GetResult().Should().BeTrue("the outgoing cockpit released within the wait, so the restart must take the claim");
    }

    [Fact]
    public void TryAcquire_ForADevelopmentBuild_DoesNotHonourTheClaim()
    {
        var claimName = UniqueClaimName();
        using var production = new CockpitHoldingTheClaim(claimName);

        using var development = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: true, claimName);

        development.Should().NotBeNull("a dotnet run is meant to start beside the cockpit hosting the session that built it");
    }

    [Fact]
    public void TryAcquire_ForADevelopmentBuild_DoesNotTakeTheClaimEither()
    {
        var claimName = UniqueClaimName();
        using var development = new CockpitHoldingTheClaim(claimName, isDevelopmentBuild: true);

        using var production = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName);

        production.Should().NotBeNull("a debug run left open must not be what keeps the real cockpit from starting");
    }

    /// <summary>Another cockpit, started and left open on a thread of its own, until disposed.</summary>
    private sealed class CockpitHoldingTheClaim : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public CockpitHoldingTheClaim(string claimName, bool isDevelopmentBuild = false)
        {
            var taken = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var guard = SingleInstanceGuard.TryAcquire(isDevelopmentBuild, claimName);
                taken.Set();
                _release.Wait();
            })
            {
                IsBackground = true,
            };

            _thread.Start();
            taken.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
        }
    }
}
