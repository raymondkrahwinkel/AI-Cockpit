using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;

namespace Cockpit.App.Services;

// AC-5/AC-1013: Turns an OS screen lock into an app lock, deciding per event whether to drive the unlock flow. It is a pure UI
// lock (the encryption key stays in memory so already-running agents keep working) that only fires when encryption
// is on, the app is unlocked, and the operator opted in, and is idempotent against duplicate lock events.
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

    // AC-1013: Supplied by `App` at startup — showing the unlock window is the view layer's job. Its task completes when
    // unlocked again, reopening the idempotence guard. Null until wired means a lock event is dropped.
    public Func<Task>? LockAction { get; set; }

    // Gives the unlock screen the keyboard back once the operator is on their own desktop again (AC-187). Supplied by
    // `App` like `LockAction`, because the window is the view layer's. Null until wired — the screen
    // then simply stays as it was shown.
    public Action? RestoreFocusAction { get; set; }

    // Subscribes to the monitor and registers it with the OS. Safe to call once; a second call is a no-op.
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

    // The gate, made awaitable so the tests can drive it directly. Returns true when this event actually locked the
    // app. Any exception from the protection read or the lock action is swallowed to a log line — a screen-lock
    // handler that throws would take the event thread with it, and failing to lock is not worth a crash.
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

    // AC-187/AC-1013: The screen came up while the OS lock desktop couldn't take focus; this is the first moment the
    // operator's desktop is back, so it hands the unlock screen the keyboard — otherwise the modal is unfocusable.
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
