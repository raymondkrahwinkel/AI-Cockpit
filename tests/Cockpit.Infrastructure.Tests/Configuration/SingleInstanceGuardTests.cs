using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Configuration;

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

        Assert.NotNull(guard);
    }

    [Fact]
    public void TryAcquire_WhileAnotherCockpitHoldsTheClaim_Refuses()
    {
        var claimName = UniqueClaimName();
        using var other = new CockpitHoldingTheClaim(claimName);

        var second = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName);

        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_AfterTheHolderReleasedTheClaim_TakesItAgain()
    {
        var claimName = UniqueClaimName();
        new CockpitHoldingTheClaim(claimName).Dispose();

        using var next = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName);

        Assert.NotNull(next);
    }

    [Fact]
    public void IsHeldByAnotherCockpit_WhileAnotherCockpitHoldsTheClaim_SaysSo()
    {
        var claimName = UniqueClaimName();
        using var other = new CockpitHoldingTheClaim(claimName);

        Assert.True(SingleInstanceGuard.IsHeldByAnotherCockpit(claimName));
    }

    [Fact]
    public void IsHeldByAnotherCockpit_WhenNothingHoldsTheClaim_SaysSo()
    {
        Assert.False(SingleInstanceGuard.IsHeldByAnotherCockpit(UniqueClaimName()));
    }

    /// <summary>
    /// The reading has to go back to false once the holder is gone, or the staged-update decision it gates (AC-738)
    /// would be permanently stuck at "some other cockpit is running" after the first launch this machine ever had.
    /// </summary>
    [Fact]
    public void IsHeldByAnotherCockpit_AfterTheHolderReleasedTheClaim_SaysNobodyHoldsIt()
    {
        var claimName = UniqueClaimName();
        new CockpitHoldingTheClaim(claimName).Dispose();

        Assert.False(SingleInstanceGuard.IsHeldByAnotherCockpit(claimName));
    }

    /// <summary>
    /// Asking must not leave a handle behind. A named claim lives as long as any handle to it does, so a reading that
    /// kept one would answer "another cockpit is running" forever after — and the staged-update decision it gates
    /// (AC-738) would never apply an update again on that machine.
    /// </summary>
    [Fact]
    public void IsHeldByAnotherCockpit_AskedWhileAnotherCockpitHeldIt_SaysNobodyDoesOnceThatCockpitIsGone()
    {
        var claimName = UniqueClaimName();
        var other = new CockpitHoldingTheClaim(claimName);

        Assert.True(SingleInstanceGuard.IsHeldByAnotherCockpit(claimName));

        other.Dispose();

        Assert.False(SingleInstanceGuard.IsHeldByAnotherCockpit(claimName));
    }

    [Fact]
    public void TryAcquire_WithAWait_WhileAnotherCockpitStillHoldsTheClaim_StillRefusesOnceItTimesOut()
    {
        var claimName = UniqueClaimName();
        using var other = new CockpitHoldingTheClaim(claimName);

        var second = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName, TimeSpan.FromMilliseconds(200));

        Assert.Null(second);
    }

    [Fact]
    public async Task TryAcquire_WithAWait_WhenTheHolderReleasesDuringIt_WinsTheHandoff()
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
        await Task.Delay(300);
        other.Dispose();

        Assert.True(await handoff, "the outgoing cockpit released within the wait, so the restart must take the claim");
    }

    [Fact]
    public void TryAcquire_ForADevelopmentBuild_DoesNotHonourTheClaim()
    {
        var claimName = UniqueClaimName();
        using var production = new CockpitHoldingTheClaim(claimName);

        using var development = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: true, claimName);

        Assert.NotNull(development);
    }

    [Fact]
    public void TryAcquire_ForADevelopmentBuild_DoesNotTakeTheClaimEither()
    {
        var claimName = UniqueClaimName();
        using var development = new CockpitHoldingTheClaim(claimName, isDevelopmentBuild: true);

        using var production = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName);

        Assert.NotNull(production);
    }

    /// <summary>
    /// The claim is keyed on the state root (AC-1217), so two instances pointed at roots of their own do not
    /// block each other — and two pointed at one root still do, however they spell it.
    /// </summary>
    /// <remarks>
    /// The spelling cases are the whole risk here: a claim derived straight from the string would be evaded by
    /// typing a trailing separator or a different case, which reads as isolation and is not.
    /// </remarks>
    [Fact]
    public void ClaimNameFor_TheDefaultRoot_IsTheNameEveryEarlierVersionUsed()
    {
        // Not cosmetic: during the one upgrade that introduces this change, the running old cockpit and the new
        // one still share a state directory, and they only see each other while the name is unchanged.
        Assert.Equal("AI-Cockpit-single-instance", SingleInstanceGuard.ClaimNameFor(CockpitBuild.DefaultStateRoot));
    }

    [Fact]
    public void ClaimNameFor_TheDefaultRootSpeltOutInFull_IsStillTheDefaultClaim()
    {
        // Pointing the variable at the very directory the cockpit already uses must not buy a second claim: those
        // two instances share everything, so this is exactly when the guard has to bite.
        var spelt = CockpitBuild.DefaultStateRoot + Path.DirectorySeparatorChar;

        Assert.Equal(SingleInstanceGuard.ClaimNameFor(CockpitBuild.DefaultStateRoot), SingleInstanceGuard.ClaimNameFor(spelt));
    }

    [Fact]
    public void ClaimNameFor_TwoDifferentRoots_AreDifferentClaims()
    {
        Assert.NotEqual(
            SingleInstanceGuard.ClaimNameFor(Path.Combine(Path.GetTempPath(), "cockpit-a")),
            SingleInstanceGuard.ClaimNameFor(Path.Combine(Path.GetTempPath(), "cockpit-b")));
    }

    [Fact]
    public void ClaimNameFor_ARootWithAndWithoutATrailingSeparator_IsOneClaim()
    {
        var root = Path.Combine(Path.GetTempPath(), "cockpit-normalise");

        Assert.Equal(
            SingleInstanceGuard.ClaimNameFor(root),
            SingleInstanceGuard.ClaimNameFor(root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ClaimNameFor_ARootWithARelativeSegment_IsTheClaimOfTheDirectoryItLandsIn()
    {
        var root = Path.Combine(Path.GetTempPath(), "cockpit-relative");
        var roundabout = Path.Combine(root, "sub", "..");

        Assert.Equal(SingleInstanceGuard.ClaimNameFor(root), SingleInstanceGuard.ClaimNameFor(roundabout));
    }

    [Fact]
    public void ClaimNameFor_OneRootInDifferentCase_IsOneClaimOnWindows()
    {
        // Windows compares paths case-insensitively, so C:\Temp\X and c:\temp\x are one directory and must be one
        // claim. Linux and macOS are left alone: there they can genuinely be two directories.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "Cockpit-Case");

        Assert.Equal(SingleInstanceGuard.ClaimNameFor(root), SingleInstanceGuard.ClaimNameFor(root.ToUpperInvariant()));
    }

    /// <summary>
    /// The name has to be the same in every process that resolves the same root, which rules out
    /// <see cref="string.GetHashCode()"/> — .NET seeds it per process, so two cockpits would never collide.
    /// </summary>
    [Fact]
    public void ClaimNameFor_ARoot_IsAMutexNameAndNotAPath()
    {
        var name = SingleInstanceGuard.ClaimNameFor(Path.Combine(Path.GetTempPath(), "cockpit-shape"));

        // Backslash is reserved in a mutex name; the scope is set through NamedWaitHandleOptions instead.
        Assert.DoesNotContain('\\', name);
        Assert.StartsWith("AI-Cockpit-single-instance-", name, StringComparison.Ordinal);
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
