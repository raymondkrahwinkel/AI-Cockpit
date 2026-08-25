using Cockpit.Core.Abstractions.Hotkeys;

namespace Cockpit.Infrastructure.Hotkeys;

// Claims a hotkey with a named, per-user `Mutex` (AC-71: two live instances, e.g. prod + dev build,
// each arming the same key with neither aware of the other). A mutex per hotkey id sees across instances
// on all three platforms, and the kernel releases it on process death; each claim gets its own long-lived `_ClaimThread`, since a `Mutex` may only be released by the thread that acquired it.
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

    // Owns one named `Mutex` for its entire lifetime on one dedicated OS thread: the thread waits on the
    // mutex first, so acquire and (once `Dispose` signals it) release both run on that same thread,
    // satisfying the mutex's thread affinity regardless of which thread-pool thread called in.
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

        // Starts the thread and waits for its first acquire attempt, returning null when another process already holds the mutex.
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
