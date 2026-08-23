namespace Cockpit.Infrastructure.Configuration;

// AC-4: the system-wide claim that this is the only cockpit running — a second start stands down before
// its startup housekeeping deletes files or plugins the first instance still has live. A named mutex,
// not a PID lock-file, since the kernel cleans it up on a killed process on all three platforms.
public sealed class SingleInstanceGuard : IDisposable
{
    // No `Global\` prefix: backslash is reserved in a mutex name, and the scope is set through
    // `NamedWaitHandleOptions` instead, which the prefix cannot express.
    private const string ClaimName = "AI-Cockpit-single-instance";

    // CurrentSessionOnly=false because on Unix every shell is its own session, and the default would scope the claim
    // to one of them: a cockpit started from a terminal and one started from the desktop launcher would not see each
    // other. CurrentUserOnly keeps the backing file under this user's own /tmp/.dotnet-uidN/, out of anyone's reach.
    private static readonly NamedWaitHandleOptions ClaimOptions = new() { CurrentUserOnly = true, CurrentSessionOnly = false };

    // Null for a development build, which holds no claim. See `TryAcquire(bool)`.
    private readonly Mutex? _claim;

    private SingleInstanceGuard(Mutex? claim) => _claim = claim;

    // Claims the right to run, or reports another cockpit already has it. `isDevelopmentBuild` takes no
    // claim and honours none, since a dev build's state lives elsewhere and cannot collide anyway.
    // Null means another cockpit holds the claim; otherwise a guard that holds it until disposed.
    public static SingleInstanceGuard? TryAcquire(bool isDevelopmentBuild) => TryAcquire(isDevelopmentBuild, ClaimName);

    // As `TryAcquire(bool)`, but waits up to `claimWait` instead of giving up instantly. A restart's new
    // process starts while the old one is still shutting down and holding the claim, so it needs the wait
    // to avoid losing the race; a plain double-launch keeps zero wait and stands down at once.
    public static SingleInstanceGuard? TryAcquire(bool isDevelopmentBuild, TimeSpan claimWait) =>
        TryAcquire(isDevelopmentBuild, ClaimName, claimWait);

    // Whether some other cockpit already holds the claim, asked without taking it (AC-738). This runs before the
    // guard itself does — a launch that is about to stand down must not apply a staged update, because applying one
    // force-stops every process in the installation directory, the running cockpit included.
    public static bool IsHeldByAnotherCockpit() => IsHeldByAnotherCockpit(ClaimName);

    internal static bool IsHeldByAnotherCockpit(string claimName)
    {
        if (!Mutex.TryOpenExisting(claimName, ClaimOptions, out var existing))
        {
            return false;
        }

        existing.Dispose();

        return true;
    }

    internal static SingleInstanceGuard? TryAcquire(bool isDevelopmentBuild, string claimName, TimeSpan claimWait = default)
    {
        if (isDevelopmentBuild)
        {
            return new SingleInstanceGuard(claim: null);
        }

        var claim = new Mutex(false, claimName, ClaimOptions, out _);

        try
        {
            if (claim.WaitOne(claimWait))
            {
                return new SingleInstanceGuard(claim);
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing (crash, kill -9); the wait succeeded and the claim
            // is ours. Letting this exception escape would rebuild the exact problem a lock-file was rejected
            // to avoid: a cockpit that won't start again after crashing once.
            return new SingleInstanceGuard(claim);
        }

        claim.Dispose();

        return null;
    }

    public void Dispose()
    {
        _claim?.ReleaseMutex();
        _claim?.Dispose();
    }
}
