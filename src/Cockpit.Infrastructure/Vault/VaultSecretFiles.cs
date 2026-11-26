using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Infrastructure.Vault;

internal sealed class VaultSecretFiles(string runtimeDirectory) : IDisposable
{
    private const uint CloseOnExec = 1;
    private const string WindowsRunPrefix = ".cockpit-vault-run-";
    private const int InvalidParameter = 87;
    private const int SharingViolation = unchecked((int)0x80070020);
    private readonly List<FileStream> _streams = [];
    private string? _windowsDirectory;

    internal static string RuntimeDirectoryFor(string configFilePath) => Path.Combine(Path.GetDirectoryName(configFilePath)
        ?? throw new InvalidOperationException("Vault configuration has no directory."), "runtime", "vault");

    public string Add(string value)
    {
        if (OperatingSystem.IsWindows())
        {
            if (_windowsDirectory is null)
            {
                using var host = Process.GetCurrentProcess();
                // The start time distinguishes a live owner from a process that reused its PID.
                string name = FormattableString.Invariant($"{WindowsRunPrefix}{host.Id}-{host.StartTime.ToUniversalTime().Ticks}-{Guid.NewGuid():N}");
                CockpitConfigPath.EnsurePrivateDirectory(runtimeDirectory);
                _windowsDirectory = Path.Combine(runtimeDirectory, name);
                CockpitConfigPath.EnsurePrivateDirectory(_windowsDirectory);
            }

            string filePath = Path.Combine(_windowsDirectory, Guid.NewGuid().ToString("N"));
            using var writer = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            WindowsPrivateAccess.Restrict(filePath, directory: false);
            writer.Write(Encoding.UTF8.GetBytes(value));
            return filePath;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The private file channel requires Windows or Linux.");
        }

        // AC-2361: memfd has no directory entry, so cleanup also survives host death and unusual mount semantics.
        int descriptor = _MemfdCreate("cockpit-vault", CloseOnExec);
        if (descriptor < 0)
        {
            throw new IOException("Could not allocate a private memory file.");
        }

        var stream = new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), FileAccess.ReadWrite);
        _streams.Add(stream);
        string path = Path.Combine("/proc", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "fd",
            descriptor.ToString(CultureInfo.InvariantCulture));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.Flush();
        stream.Position = 0;
        return path;
    }

    internal static void SweepStale(string runtimeDirectory, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(runtimeDirectory, WindowsRunPrefix + "*"))
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith(WindowsRunPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = name[WindowsRunPrefix.Length..].Split('-');
                if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int ownerId) || ownerId <= 0
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long started) || started <= 0
                    || !Guid.TryParseExact(parts[2], "N", out _))
                {
                    continue;
                }

                try
                {
                    if (!IsOwnerRunning(ownerId, started))
                    {
                        DeleteRunDirectory(directory);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    logger.LogWarning("Could not clean up a leftover vault run directory ({ExceptionType}).", exception.GetType().Name);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not scan for leftover vault run directories ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private static bool IsOwnerRunning(int processId, long startTime)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTime;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception { NativeErrorCode: InvalidParameter })
        {
            return false;
        }
    }

    private static void DeleteRunDirectory(string directory)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException exception) when (exception.HResult == SharingViolation && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
            {
                // Windows job termination can still be closing a descendant's file handle.
                Thread.Sleep(20);
            }
        }
    }

    [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true)]
    private static extern int _MemfdCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);

    public void Dispose()
    {
        foreach (FileStream stream in _streams)
        {
            stream.Dispose();
        }

        if (_windowsDirectory is not null)
        {
            DeleteRunDirectory(_windowsDirectory);
        }
    }
}
