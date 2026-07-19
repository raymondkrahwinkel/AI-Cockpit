namespace Cockpit.Core.Secrets;

/// <summary>
/// Watches the operating system's own screen lock, so AI-Cockpit can lock itself when the desktop locks (AC-5) —
/// clear the in-memory key and ask for the encryption password again, exactly as at startup.
/// <para>
/// One event-source, three OS-specific implementations chosen at runtime (Windows session notifications, the macOS
/// distributed <c>screenIsLocked</c> notification, Linux systemd-logind <c>Session.Lock</c>/<c>Unlock</c>), plus a
/// <see cref="NullScreenLockMonitor"/> for anything else. Deliberately just a trigger: it does not touch the key or
/// the UI — the coordinator above it decides, per event, whether the feature applies and reuses the existing unlock
/// flow. That keeps the untestable native layer as thin as it can be, and keeps the gate (encryption on, option on)
/// in one testable place rather than smeared across three platform files.
/// </para>
/// <para>
/// <see cref="StartAsync"/> registers with the OS; <see cref="IDisposable.Dispose"/> unregisters. A monitor that
/// cannot register (no logind, an unsupported desktop) fails safe — it simply never raises <see cref="Locked"/>, so
/// the app behaves as if the feature were off rather than crashing the launch over a missing OS facility.
/// </para>
/// </summary>
public interface IScreenLockMonitor : IDisposable
{
    /// <summary>The OS reported the screen has locked. May fire more than once for one lock (screensaver then lock, two D-Bus sources); the coordinator is idempotent so a duplicate costs nothing.</summary>
    event EventHandler? Locked;

    /// <summary>The OS reported the screen has unlocked. Carried for completeness; AC-5 does not auto-unlock the cockpit — the password screen stays until the operator types it — so listeners may ignore this.</summary>
    event EventHandler? Unlocked;

    /// <summary>Registers with the OS's lock notifications. Idempotent, and safe to call even where the feature cannot be provided — it then does nothing and never raises an event.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The fail-safe monitor: it observes nothing and raises nothing. Registered on platforms with no lock facility this
/// build supports, so the runtime selection always yields a working object and the coordinator's gate simply never
/// fires — the feature is absent, not broken.
/// </summary>
public sealed class NullScreenLockMonitor : IScreenLockMonitor
{
    /// <inheritdoc />
    /// <remarks>Never raised. Kept so the type satisfies the contract; the empty add/remove keep the analyzer quiet without a backing field that would read as "someone forgot to fire this".</remarks>
    public event EventHandler? Locked
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler? Unlocked
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing was ever registered, so there is nothing to release.
    }
}
