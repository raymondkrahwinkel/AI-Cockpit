using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Configuration;

// AC-1486: every process in one installation keeps a private file open. The update path reads the other
// files under a short mutex, so it never replaces binaries while another process from this installation lives.
public sealed class InstallationInstanceGuard : IDisposable
{
    private const string InstancesDirectoryName = ".instances";
    private static readonly NamedWaitHandleOptions UpdateCheckOptions = new() { CurrentUserOnly = true, CurrentSessionOnly = false };
    private static string? _currentClaimPath;

    private readonly FileStream? _claim;
    private readonly string? _claimPath;

    private InstallationInstanceGuard(FileStream? claim, string? claimPath)
    {
        _claim = claim;
        _claimPath = claimPath;
    }

    public static InstallationInstanceGuard Acquire(ILogger logger) => Acquire(AppContext.BaseDirectory, logger);

    public static bool IsAnotherInstanceRunning() => IsAnotherInstanceRunning(AppContext.BaseDirectory, _currentClaimPath);

    internal static InstallationInstanceGuard Acquire(string installationDirectory, ILogger? logger = null)
    {
        try
        {
            var instancesDirectory = InstancesDirectory(installationDirectory);
            Directory.CreateDirectory(instancesDirectory);

            var claimPath = Path.Combine(instancesDirectory, $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks}.lock");
            var claim = new FileStream(claimPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            Interlocked.Exchange(ref _currentClaimPath, claimPath);

            return new InstallationInstanceGuard(claim, claimPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "Could not create an installation update claim in {InstallationDirectory}; self-updates are disabled for this process.", installationDirectory);

            return new InstallationInstanceGuard(claim: null, claimPath: null);
        }
    }

    internal static bool IsAnotherInstanceRunning(string installationDirectory, string? ownClaimPath = null)
    {
        var normalizedDirectory = NormalizeDirectory(installationDirectory);
        using var updateCheck = new Mutex(false, $"AI-Cockpit-installation-update-{Fingerprint(normalizedDirectory)}", UpdateCheckOptions, out _);
        var acquired = false;

        try
        {
            try
            {
                acquired = updateCheck.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                return true;
            }

            var instancesDirectory = InstancesDirectory(installationDirectory);
            if (!Directory.Exists(instancesDirectory))
            {
                return false;
            }

            foreach (var claimPath in Directory.EnumerateFiles(instancesDirectory, "*.lock"))
            {
                if (string.Equals(claimPath, ownClaimPath, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var claim = new FileStream(claimPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    File.Delete(claimPath);
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException)
                {
                    return true;
                }
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        finally
        {
            if (acquired)
            {
                updateCheck.ReleaseMutex();
            }
        }
    }

    public void Dispose()
    {
        _claim?.Dispose();
        if (_claimPath is not null)
        {
            Interlocked.CompareExchange(ref _currentClaimPath, null, _claimPath);
        }
    }

    private static string InstancesDirectory(string installationDirectory) =>
        Path.Combine(installationDirectory, InstancesDirectoryName);

    private static string NormalizeDirectory(string installationDirectory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));

        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    private static string Fingerprint(string normalizedDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDirectory)))[..16];
}
