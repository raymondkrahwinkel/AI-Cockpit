using System.Runtime.InteropServices;
using System.Text;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Infrastructure.Vault;

internal sealed class VaultSecretFiles(string runtimeDirectory) : IDisposable
{
    private const uint CloseOnExec = 1;
    private readonly string _directory = Path.Combine(runtimeDirectory, ".cockpit-vault-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileStream> _streams = [];

    public string Add(string value)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The private file channel requires Windows or Linux.");
        }

        FileStream stream;
        string path;
        if (OperatingSystem.IsLinux())
        {
            // AC-2361: memfd has no directory entry, so cleanup also survives host death and unusual mount semantics.
            int descriptor = _MemfdCreate("cockpit-vault", CloseOnExec);
            if (descriptor < 0)
            {
                throw new IOException("Could not allocate a private memory file.");
            }

            stream = new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), FileAccess.ReadWrite);
            _streams.Add(stream);
            path = Path.Combine("/proc", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "fd",
                descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        else
        {
            CockpitConfigPath.EnsurePrivateDirectory(runtimeDirectory);
            CockpitConfigPath.EnsurePrivateDirectory(_directory);
            path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.DeleteOnClose,
            });
            _streams.Add(stream);
            CockpitConfigPath.RestrictExistingFile(path);
        }

        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.Flush();
        stream.Position = 0;
        return path;
    }

    [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true)]
    private static extern int _MemfdCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);

    public void Dispose()
    {
        foreach (FileStream stream in _streams)
        {
            stream.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory);
        }
    }
}
