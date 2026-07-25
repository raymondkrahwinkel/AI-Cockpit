using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;

namespace Cockpit.App.Services;

/// <summary>
/// Turns an OS screen lock into an app lock (AC-5), and is the one place the decision is made. The native
/// <see cref="IScreenLockMonitor"/> below it only reports "the screen locked"; this decides — per event — whether
/// that should lock the cockpit, and if so drives the existing unlock flow.
/// <para>
/// This is a pure UI lock: it puts the unlock screen in front of the running cockpit but leaves the encryption key
/// in memory. The point is that the operator has to re-enter the password to touch the UI again, while agents that
/// are already running keep working — a background config write still needs the key, so clearing it would block a
/// writing agent for no security gain (the process is the same, and the screen already guards the UI).
/// </para>
/// <para>
/// A lock only happens when all of it holds: encryption is on (there is a password to re-ask for), the app is
/// currently unlocked (a locked app is already where a lock would send it), and the operator left the option on.
/// That gate is why this class exists apart from the platform monitors — it is the testable half, exercised with a
/// fake monitor and a fake protection service, while the native hookup that cannot be unit-tested stays as thin as
/// possible. Re-read on every event, not cached, so turning the option or encryption off takes effect at once
/// without restarting the monitor.
/// </para>
/// <para>
/// Idempotent: a single physical lock can raise several events (a screensaver then the lock; two D-Bus sources on
/// Linux), and while the unlock screen is already up another lock must not stack a second one. A single guard admits
/// one lock at a time and clears when the operator has unlocked again — which is when <see cref="LockAction"/>'s task
/// completes.
/// </para>
/// </summary>
internal sealed class ScreenLockCoordinator : ISingletonService, IDisposable
{
    private readonly IScreenLockMonitor _monitor;
    private readonly ISecretProtectionService _protection;
    private readonly IScreenLockSettingsStore _settings;
    private readonly ILogger<ScreenLockCoordinator> _logger;

    // 0 = not currently locking, 1 = a lock is in effect (the unlock screen is up). Guarded with Interlocked so two
    // near-simultaneous lock events cannot both pass.
    private int _locking;

    private bool _started;

    public ScreenLockCoordinator(
        IScreenLockMonitor monitor,
        ISecretProtectionService protection,
        IScreenLockSettingsStore settings,
        ILogger<ScreenLockCoordinator> logger)
    {
        _monitor = monitor;
        _protection = protection;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// What actually locks the app: showing the unlock window over the running cockpit is the view layer's job, so it
    /// is supplied by <c>App</c> at startup. Its returned task completes when the operator has unlocked again, which is
    /// what lets the idempotence guard reopen. Null until wired — with no way to show the screen, a lock event is
    /// simply dropped.
    /// </summary>
    public Func<Task>? LockAction { get; set; }

    /// <summary>
    /// Gives the unlock screen the keyboard back once the operator is on their own desktop again (AC-187). Supplied by
    /// <c>App</c> like <see cref="LockAction"/>, because the window is the view layer's. Null until wired — the screen
    /// then simply stays as it was shown.
    /// </summary>
    public Action? RestoreFocusAction { get; set; }

    /// <summary>Subscribes to the monitor and registers it with the OS. Safe to call once; a second call is a no-op.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _monitor.Locked += OnLocked;
        _monitor.Unlocked += OnUnlocked;
        await _monitor.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnLocked(object? sender, EventArgs e) => _ = HandleLockAsync();

    private void OnUnlocked(object? sender, EventArgs e) => HandleUnlock();

    /// <summary>
    /// The gate, made awaitable so the tests can drive it directly. Returns true when this event actually locked the
    /// app. Any exception from the protection read or the lock action is swallowed to a log line — a screen-lock
    /// handler that throws would take the event thread with it, and failing to lock is not worth a crash.
    /// </summary>
    internal async Task<bool> HandleLockAsync()
    {
        try
        {
            if (LockAction is null)
            {
                return false;
            }

            var status = await _protection.GetStatusAsync().ConfigureAwait(false);

            // Encryption off — nothing to re-ask for. Already locked — a lock event while the app is not unlocked is
            // nothing to act on. Either way this event is not ours.
            if (!status.Enabled || !status.Unlocked)
            {
                return false;
            }

            if (!(await _settings.LoadAsync().ConfigureAwait(false)).LockWhenOperatingSystemLocks)
            {
                return false;
            }

            // Admit exactly one lock. A duplicate event (or one arriving while the screen is already up) turns back
            // here rather than stacking a second window.
            if (Interlocked.CompareExchange(ref _locking, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                // Pure UI lock: put the unlock screen in front, but leave the key in memory so a running agent's
                // config write is not blocked. The screen is what re-asks for the password before the UI can be
                // touched again; the key staying put is what keeps the agents behind it working.
                _logger.LogInformation("The OS screen locked; the cockpit locked its UI and is asking for the encryption password again.");

                await LockAction().ConfigureAwait(false);

                return true;
            }
            finally
            {
                Interlocked.Exchange(ref _locking, 0);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Handling an OS screen lock failed; the cockpit was not locked.");
            return false;
        }
    }

    /// <summary>
    /// The other half of the lock (AC-187): the screen went up while the OS was still on its own lock desktop, where a
    /// window cannot be activated or typed into. This is the first moment the operator's desktop is back, so it is the
    /// moment to hand the unlock screen the keyboard — without it the modal sits there unfocusable and the only way out
    /// is killing the app. Returns true when focus was actually handed back, which is what the tests assert.
    /// </summary>
    internal bool HandleUnlock()
    {
        // Only for a lock of ours that is still up. An unlock event without one (the feature is off, the operator
        // never locked the app, the screen was already dismissed) is not ours to act on.
        if (RestoreFocusAction is null || Volatile.Read(ref _locking) == 0)
        {
            return false;
        }

        try
        {
            RestoreFocusAction();

            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Restoring focus to the unlock screen after the OS unlocked failed.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            _monitor.Locked -= OnLocked;
            _monitor.Unlocked -= OnUnlocked;
        }

        _monitor.Dispose();
    }
}
