using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Infrastructure.Vault;

internal sealed class VaultSecretFiles(ILogger logger) : IDisposable
{
    private const uint CloseOnExec = 1;
    private const string WindowsRunPrefix = ".cockpit-vault-run-utc-";
    private const int SharingViolation = unchecked((int)0x80070020);
    private readonly List<FileStream> _streams = [];
    private string? _windowsDirectory;

    internal static string RuntimeDirectoryFor(string configFilePath, bool machineLocal)
    {
        string stateRoot = Path.GetDirectoryName(configFilePath)
            ?? throw new InvalidOperationException("Vault configuration has no directory.");
        if (!OperatingSystem.IsWindows() || !machineLocal)
        {
            return Path.Combine(stateRoot, "runtime", "vault");
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(local))
        {
            throw new UnauthorizedAccessException("The machine-local vault runtime directory could not be resolved.");
        }

        string runtime = Path.Combine(local, CockpitBuild.StateFolder, "runtime", "vault");
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateRoot));
        if (string.Equals(normalized, Path.GetFullPath(CockpitBuild.DefaultStateRoot), StringComparison.OrdinalIgnoreCase))
        {
            return runtime;
        }

        string instance = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant())))[..32];
        return Path.Combine(runtime, instance);
    }

    public string Add(string value, string runtimeDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (_windowsDirectory is null)
            {
                using var host = Process.GetCurrentProcess();
                // The start time distinguishes a live owner from a process that reused its PID.
                string name = FormattableString.Invariant($"{WindowsRunPrefix}{host.Id}-{GetCreationTime(host.SafeHandle)}-{Guid.NewGuid():N}");
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

    internal static void SweepStale(string configFilePath, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            string runtimeDirectory = RuntimeDirectoryFor(configFilePath, machineLocal: true);
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
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
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
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        using (process)
        {
            return !process.HasExited && GetCreationTime(process.SafeHandle) == startTime;
        }
    }

    private static long GetCreationTime(SafeProcessHandle process)
    {
        if (!_GetProcessTimes(process, out var creation, out _, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
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

    [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _GetProcessTimes(SafeProcessHandle process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

    public void Dispose()
    {
        foreach (FileStream stream in _streams)
        {
            stream.Dispose();
        }

        if (_windowsDirectory is not null)
        {
            try
            {
                DeleteRunDirectory(_windowsDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove a vault run directory ({ExceptionType}).", exception.GetType().Name);
            }
        }
    }
}
