namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// The system-wide claim that this is the only cockpit running (AC-4). A second start finds the claim taken and
/// stands down before it touches anything the first one owns.
/// </summary>
/// <remarks>
/// <para>
/// Two cockpits over one state directory is not a tidiness problem: they share <c>cockpit.json</c>, and each
/// writes it whole. The second one's startup housekeeping deletes the <c>--mcp-config</c> files the first one's
/// live sessions are still reading, and its bundled-plugin install deletes plugin directories the first one has
/// loaded. This is why the guard runs before any of that, and not after.
/// </para>
/// <para>
/// A named mutex rather than a PID lock-file because it is the only mechanism the kernel cleans up on all three
/// platforms when a process is killed outright. A lock-file has to guess whether the PID in it is still alive,
/// and a wrong guess leaves the app unstartable — worse than having no guard.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// No <c>Global\</c> prefix: backslash is reserved in a mutex name, and the scope is set through
    /// <see cref="NamedWaitHandleOptions"/> instead, which the prefix cannot express.
    /// </summary>
    private const string ClaimName = "AI-Cockpit-single-instance";

    /// <summary>Null for a development build, which holds no claim. See <see cref="TryAcquire(bool)"/>.</summary>
    private readonly Mutex? _claim;

    private SingleInstanceGuard(Mutex? claim) => _claim = claim;

    /// <summary>
    /// Claims the right to run, or reports that another cockpit already has it.
    /// </summary>
    /// <param name="isDevelopmentBuild">
    /// A development build takes no claim and honours none: it is meant to run beside the production cockpit,
    /// including the one hosting the session that is rebuilding it. Its state lives elsewhere
    /// (<see cref="Cockpit.Core.Configuration.CockpitBuild.StateFolder"/>), so the two cannot collide anyway.
    /// </param>
    /// <returns>
    /// Null when another cockpit holds the claim — the caller must not start. Otherwise a guard that holds the
    /// claim until it is disposed.
    /// </returns>
    public static SingleInstanceGuard? TryAcquire(bool isDevelopmentBuild) => TryAcquire(isDevelopmentBuild, ClaimName);

    /// <summary>
    /// As <see cref="TryAcquire(bool)"/>, but waits up to <paramref name="claimWait"/> for the claim to come free
    /// instead of giving up the instant it is taken. A restart hands the claim from the old cockpit to the new one
    /// (<see cref="Cockpit.App.Services.AppRestartService"/>): the new process starts while the old one is still
    /// shutting down and holding the claim, so without a wait it would lose the race and refuse to start. A plain
    /// double-launch keeps the zero wait and still stands down at once.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(bool isDevelopmentBuild, TimeSpan claimWait) =>
        TryAcquire(isDevelopmentBuild, ClaimName, claimWait);

    internal static SingleInstanceGuard? TryAcquire(bool isDevelopmentBuild, string claimName, TimeSpan claimWait = default)
    {
        if (isDevelopmentBuild)
        {
            return new SingleInstanceGuard(claim: null);
        }

        // CurrentSessionOnly=false because on Unix every shell is its own session, and the default would scope the
        // claim to one of them: a cockpit started from a terminal and one started from the desktop launcher would
        // not see each other. That, and not any missing support, is the whole of the "named mutexes don't work on
        // Linux" folklore. CurrentUserOnly keeps the backing file under this user's own /tmp/.dotnet-uidN/, where
        // no other user can take our claim or interfere with it.
        var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false };
        var claim = new Mutex(false, claimName, options, out _);

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
