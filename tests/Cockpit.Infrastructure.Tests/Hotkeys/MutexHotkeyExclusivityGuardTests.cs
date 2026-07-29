using Cockpit.Infrastructure.Hotkeys;

namespace Cockpit.Infrastructure.Tests.Hotkeys;

/// <summary>
/// The exclusivity claim behind AC-71: two live cockpit instances competing for the same hotkey id, modelled as
/// two guard instances since each cockpit process would construct its own. A named mutex is a kernel object
/// shared by name, but ownership is per-thread — two <see cref="MutexHotkeyExclusivityGuard"/>s on the *same*
/// thread would trivially both succeed (Windows/.NET mutexes are reentrant for their owning thread), which
/// proves nothing about two processes. <see cref="_OnAnotherThreadAsync{T}"/> is what actually stands in for
/// "another process": a different OS thread is exactly what two cockpit instances would be.
/// </summary>
public class MutexHotkeyExclusivityGuardTests
{
    /// <summary>A key unique per test run — two test methods must not contend for the same OS-level mutex name.</summary>
    private static string _HotkeyId() => $"test-{Guid.NewGuid():N}";

    private static Task<T> _OnAnotherThreadAsync<T>(Func<T> action) => Task.Factory.StartNew(
        action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Fact]
    public void ANobodyElseHolds_IsClaimed()
    {
        var guard = new MutexHotkeyExclusivityGuard();

        using var claim = guard.TryAcquire(_HotkeyId());

        Assert.NotNull(claim);
    }

    [Fact]
    public async Task AKeyAnotherInstanceHolds_IsRefused()
    {
        var hotkeyId = _HotkeyId();
        var first = new MutexHotkeyExclusivityGuard();
        var second = new MutexHotkeyExclusivityGuard();
        using var holder = new _MutexHolder(first, hotkeyId);
        await holder.AcquiredAsync();

        try
        {
            Assert.Null(second.TryAcquire(hotkeyId));
        }
        finally
        {
            await holder.ReleaseAndWaitAsync();
        }
    }

    [Fact]
    public async Task ReleasingTheClaim_LetsTheOtherInstanceAcquireIt()
    {
        var hotkeyId = _HotkeyId();
        var first = new MutexHotkeyExclusivityGuard();
        var second = new MutexHotkeyExclusivityGuard();
        using var holder = new _MutexHolder(first, hotkeyId);
        await holder.AcquiredAsync();
        Assert.Null(second.TryAcquire(hotkeyId));

        await holder.ReleaseAndWaitAsync();

        using var claim = second.TryAcquire(hotkeyId);
        Assert.NotNull(claim);
    }

    /// <summary>
    /// The exact shape <see cref="GlobalHotkeyCoordinator"/> uses it in: the claim is acquired on whichever
    /// thread-pool thread resumes an async continuation, and released — potentially much later — on a
    /// different one entirely (a settings save, a retry tick, app shutdown). A <see cref="Mutex"/> may only be
    /// released by the thread that acquired it; this must not throw, and the release must actually free the key
    /// for the next claimant, proving the guard, not just the mutex primitive, honours that constraint.
    /// </summary>
    [Fact]
    public async Task AcquiredOnOneThreadAndDisposedOnAnother_ReleasesCleanly()
    {
        var hotkeyId = _HotkeyId();
        var guard = new MutexHotkeyExclusivityGuard();

        var claim = await _OnAnotherThreadAsync(() => guard.TryAcquire(hotkeyId));
        Assert.NotNull(claim);

        var disposeException = await _OnAnotherThreadAsync(() =>
        {
            try
            {
                claim.Dispose();
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        Assert.Null(disposeException);

        var second = new MutexHotkeyExclusivityGuard();
        using var reclaimed = second.TryAcquire(hotkeyId);
        Assert.NotNull(reclaimed);
    }

    /// <summary>A settings save re-arms the same key on the same instance — that must read as "still mine", not a conflict with itself.</summary>
    [Fact]
    public void AcquiringAKeyThisInstanceAlreadyHolds_SucceedsAgain()
    {
        var hotkeyId = _HotkeyId();
        var guard = new MutexHotkeyExclusivityGuard();
        using var first = guard.TryAcquire(hotkeyId);

        using var second = guard.TryAcquire(hotkeyId);

        Assert.NotNull(second);
    }

    [Fact]
    public void TwoDifferentHotkeyIds_DoNotContendWithEachOther()
    {
        var guard = new MutexHotkeyExclusivityGuard();

        using var first = guard.TryAcquire(_HotkeyId());
        using var second = guard.TryAcquire(_HotkeyId());

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    /// <summary>
    /// Holds a claim on a dedicated, long-running thread until told to let go. A <see cref="Mutex"/> must be
    /// released by the thread that acquired it, so "another instance holds this, then releases it" needs the
    /// acquire and the release to happen on the same thread — just not the test method's.
    /// </summary>
    private sealed class _MutexHolder(MutexHotkeyExclusivityGuard guard, string hotkeyId) : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new();
        private readonly ManualResetEventSlim _release = new();
        private Task? _holding;

        public Task AcquiredAsync()
        {
            _holding = _OnAnotherThreadAsync(() =>
            {
                using var claim = guard.TryAcquire(hotkeyId);
                _acquired.Set();
                _release.Wait();
                return true;
            });

            return Task.Run(() => _acquired.Wait());
        }

        public async Task ReleaseAndWaitAsync()
        {
            _release.Set();
            await _holding!;
        }

        public void Dispose()
        {
            _release.Set();
            _acquired.Dispose();
            _release.Dispose();
        }
    }
}
