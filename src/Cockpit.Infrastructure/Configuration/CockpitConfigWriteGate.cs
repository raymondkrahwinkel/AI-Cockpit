namespace Cockpit.Infrastructure.Configuration;

// AC-41: the single write gate for `cockpit.json` — one lock file every writer takes so the encryption
// migration and the settings stores never interleave. Non-reentrant: `ChangePasswordAsync` takes it once.
internal static class CockpitConfigWriteGate
{
    // Holds the write gate; empty, and only its existence-while-open means anything.
    private const string LockSuffix = ".lock";

    // Generous on purpose. AC-1108: a CockpitConfigWriteBatch now holds this for its whole scope (a batched
    // Apply, measured ~23ms), not just one write — reaching this still means something is wrong, not busy.
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan GatePollInterval = TimeSpan.FromMilliseconds(20);

    // Takes the write gate for `configFilePath`, waiting for whoever holds it. Dispose the
    // returned stream to release it.
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
