namespace Cockpit.Infrastructure.Configuration;

// The system-wide claim that this is the only cockpit running (AC-4). A second start finds the claim taken and
// stands down before it touches anything the first one owns.
//
// Two cockpits over one state directory is not a tidiness problem: they share `cockpit.json`, and each
// writes it whole. The second one's startup housekeeping deletes the `--mcp-config` files the first one's
// live sessions are still reading, and its bundled-plugin install deletes plugin directories the first one has
// loaded. This is why the guard runs before any of that, and not after.
//
// A named mutex rather than a PID lock-file because it is the only mechanism the kernel cleans up on all three
// platforms when a process is killed outright. A lock-file has to guess whether the PID in it is still alive,
// and a wrong guess leaves the app unstartable — worse than having no guard.
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

    // Claims the right to run, or reports that another cockpit already has it.
    //
    // `isDevelopmentBuild`:
    // A development build takes no claim and honours none: it is meant to run beside the production cockpit,
    // including the one hosting the session that is rebuilding it. Its state lives elsewhere
    // (`Cockpit.Core.Configuration.CockpitBuild.StateFolder`), so the two cannot collide anyway.
    // Null when another cockpit holds the claim — the caller must not start. Otherwise a guard that holds the
    // claim until it is disposed.
    public static SingleInstanceGuard? TryAcquire(bool isDevelopmentBuild) => TryAcquire(isDevelopmentBuild, ClaimName);

    // As `TryAcquire(bool)`, but waits up to `claimWait` for the claim to come free
    // instead of giving up the instant it is taken. A restart hands the claim from the old cockpit to the new one
    // (`Cockpit.App.Services.AppRestartService`): the new process starts while the old one is still
    // shutting down and holding the claim, so without a wait it would lose the race and refuse to start. A plain
    // double-launch keeps the zero wait and still stands down at once.
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
            // The previous holder died without releasing — a crash, a kill -9. The wait succeeded and the claim is
            // ours; the exception is the kernel telling us who it used to belong to. Letting it escape would build
            // exactly the thing this design rejected a lock-file to avoid: a cockpit that will not start again
            // after it has crashed once.
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
