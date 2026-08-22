namespace Cockpit.Core.Secrets;

/// <summary>
/// Watches the operating system's own screen lock, so AI-Cockpit can lock itself when the desktop locks (AC-5) —
/// puts the unlock screen in front and asks for the encryption password again, leaving the key in memory so any
/// agent already running keeps working. One event-source, three OS-specific implementations chosen at runtime
/// (Windows session notifications, macOS's distributed <c>screenIsLocked</c>, Linux systemd-logind's
/// <c>LockedHint</c>), plus <see cref="NullScreenLockMonitor"/> elsewhere; deliberately just a trigger, never touching the key or UI, so the coordinator decides per event and the gate stays in one testable place. <see cref="StartAsync"/> registers, <see cref="IDisposable.Dispose"/> unregisters; a monitor that cannot register fails safe, never raising <see cref="Locked"/>.
/// </summary>
public interface IScreenLockMonitor : IDisposable
{
    /// <summary>The OS reported the screen has locked. May fire more than once for one lock (screensaver then lock, two D-Bus sources); the coordinator is idempotent so a duplicate costs nothing.</summary>
    event EventHandler? Locked;

    /// <summary>The OS reported the screen has unlocked. Does not unlock the cockpit — the password screen stays until the operator types it — but it is the first moment the operator's own desktop is back, so it is what gives that screen the keyboard (AC-187): a window shown while the OS was locked was never activated where the operator can see it.</summary>
    event EventHandler? Unlocked;

    /// <summary>Registers with the OS's lock notifications. Idempotent, and safe to call even where the feature cannot be provided — it then does nothing and never raises an event.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}

// The fail-safe monitor: it observes nothing and raises nothing. Registered on platforms with no lock facility this
// build supports, so the runtime selection always yields a working object and the coordinator's gate simply never
// fires — the feature is absent, not broken.
public sealed class NullScreenLockMonitor : IScreenLockMonitor
{
    // Never raised. Kept so the type satisfies the contract; the empty add/remove keep the analyzer quiet without a backing field that would read as "someone forgot to fire this".
    public event EventHandler? Locked
    {
        add { }
        remove { }
    }

    public event EventHandler? Unlocked
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose()
    {
        // Nothing was ever registered, so there is nothing to release.
    }
}
