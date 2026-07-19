namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// The single write gate for <c>cockpit.json</c>: one lock file next to the config that every writer takes
/// before a read-modify-write, in this process and in any other cockpit on this machine.
/// <para>
/// It used to live privately inside <see cref="CockpitConfigFileAccess"/>, where only the typed settings stores
/// reached it. The encryption migration and the awareness-banner dismissal (AC-41) rewrite the same file, so
/// they have to take the same lock — otherwise a migration could interleave with a store write and one of them
/// would silently restore the other's section. Hoisting it here makes "who serialises against whom" one fact in
/// one place: everyone locks on <c>&lt;path&gt;.lock</c>.
/// </para>
/// <para>
/// A lock file rather than a named mutex: the operating system drops it when the holder exits — including a
/// process killed mid-write — and it behaves the same on the three platforms the cockpit runs on. The lock is
/// non-reentrant (<see cref="FileShare.None"/>), so a leaf operation must never take it while already holding it:
/// re-entering deadlocks until the timeout. That is why <c>ChangePasswordAsync</c> is not gated as a whole and
/// instead lets its Disable/Enable steps each take and release the gate in turn.
/// </para>
/// </summary>
internal static class CockpitConfigWriteGate
{
    /// <summary>Holds the write gate; empty, and only its existence-while-open means anything.</summary>
    private const string LockSuffix = ".lock";

    /// <summary>Generous on purpose: a write is milliseconds, so reaching this means something is wrong, not busy.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan GatePollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Takes the write gate for <paramref name="configFilePath"/>, waiting for whoever holds it. Dispose the
    /// returned stream to release it.
    /// </summary>
    public static async Task<FileStream> AcquireAsync(string configFilePath, CancellationToken cancellationToken)
    {
        var lockFilePath = configFilePath + LockSuffix;
        var directory = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var deadline = DateTimeOffset.UtcNow + GateTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Someone else is mid-write. Theirs finishes in milliseconds — this is a settings file, not a
                // database — so waiting is cheaper than any scheme that lets both through and merges after.
                await Task.Delay(GatePollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // Long past the point where contention explains it. Failing loudly beats writing anyway: a save
                // that goes through ungated is how a section disappears, and disappearing is what this exists
                // to stop.
                throw new IOException(
                    $"Could not take the write lock on '{lockFilePath}' within {GateTimeout.TotalSeconds:F0}s; the cockpit's settings were not saved.",
                    exception);
            }
        }
    }
}
