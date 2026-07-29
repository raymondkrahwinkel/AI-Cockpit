using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Hotkeys;

/// <summary>
/// Claims a hotkey with a named, per-user <see cref="Mutex"/> — the same mechanism and reasoning as
/// <see cref="SingleInstanceGuard"/>, one level narrower. That guard keeps a second production cockpit from
/// starting at all, but a development build intentionally runs beside it (<see cref="SingleInstanceGuard.TryAcquire(bool)"/>)
/// — which is exactly the AC-71 scenario: two live instances, each arming the same key and neither aware of the
/// other. Neither <see cref="IGlobalHotkeyService"/> backend can see the other instance; a mutex per hotkey id
/// is what can, on all three platforms, and the kernel releases it the moment a process dies without disposing
/// it — a crash included — so a waiting instance never needs a restart to pick the key back up.
/// </summary>
/// <remarks>
/// A <see cref="Mutex"/> is owned by the thread that acquired it, and only that thread may release it — but
/// <see cref="TryAcquire"/> is called from an async continuation (whichever thread-pool thread happens to
/// resume <c>GlobalHotkeyCoordinator.ApplyAsync</c>) and the matching release can land on a different one
/// entirely. Each claim therefore gets its own small, long-lived <see cref="_ClaimThread"/> that does the
/// acquiring and, later, the releasing itself — <see cref="TryAcquire"/>/the returned claim's
/// <see cref="IDisposable.Dispose"/> only ever signal it across a wait handle.
/// </remarks>
internal sealed class MutexHotkeyExclusivityGuard : IHotkeyExclusivityGuard
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, _ClaimThread> _held = [];

    public IDisposable? TryAcquire(string hotkeyId)
    {
        lock (_gate)
        {
            if (_held.ContainsKey(hotkeyId))
            {
                // Already ours: a settings save re-arms the same key, and that must not read as a conflict with
                // itself.
                return new Claim(this, hotkeyId);
            }

            var thread = _ClaimThread.TryStart(hotkeyId);
            if (thread is null)
            {
                return null;
            }

            _held[hotkeyId] = thread;
            return new Claim(this, hotkeyId);
        }
    }

    private void _Release(string hotkeyId)
    {
        lock (_gate)
        {
            if (_held.Remove(hotkeyId, out var thread))
            {
                thread.Dispose();
            }
        }
    }

    private sealed class Claim(MutexHotkeyExclusivityGuard owner, string hotkeyId) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner._Release(hotkeyId);
        }
    }

    /// <summary>
    /// Owns one named <see cref="Mutex"/> for its entire lifetime, on one dedicated OS thread: the thread waits
    /// on the mutex as its very first action, so the acquire and — once <see cref="Dispose"/> signals it to stop
    /// — the release both run on that same thread, satisfying the mutex's thread affinity regardless of which
    /// thread-pool thread called in from either side.
    /// </summary>
    private sealed class _ClaimThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _release = new();
        private bool _acquired;

        private _ClaimThread(string hotkeyId)
        {
            _thread = new Thread(() => _Run(hotkeyId)) { IsBackground = true, Name = $"cockpit-hotkey-{hotkeyId}" };
            _thread.Start();
            _ready.Wait();
        }

        /// <summary>Starts the thread and waits for its first acquire attempt, returning null when another process already holds the mutex.</summary>
        public static _ClaimThread? TryStart(string hotkeyId)
        {
            var thread = new _ClaimThread(hotkeyId);
            return thread._acquired ? thread : null;
        }

        private void _Run(string hotkeyId)
        {
            // Same scoping as SingleInstanceGuard: CurrentUserOnly keeps another user's cockpit from claiming or
            // seeing this; CurrentSessionOnly=false because on Unix every shell is its own session, and the
            // default would hide a terminal-started instance from a desktop-launched one.
            var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false };
            using var mutex = new Mutex(false, $"AI-Cockpit-hotkey-{hotkeyId}", options, out _);

            try
            {
                _acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died without releasing — a crash, a kill -9. The wait still succeeded and
                // the claim is ours; the exception is only the kernel saying who it used to belong to.
                _acquired = true;
            }

            _ready.Set();

            if (!_acquired)
            {
                return;
            }

            _release.Wait();
            mutex.ReleaseMutex();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _ready.Dispose();
            _release.Dispose();
        }
    }
}
