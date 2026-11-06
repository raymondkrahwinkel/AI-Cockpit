using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Plugin.Browser.Ipc;

internal sealed record BrowserIpcFrameDescriptor(Guid Generation, Guid BufferId, int Width, int Height)
{
    internal long ByteLength
    {
        get
        {
            if (Generation == Guid.Empty || BufferId == Guid.Empty || Width <= 0 || Height <= 0 || Width > 8192 || Height > 8192)
            {
                throw new InvalidDataException("Invalid IPC frame dimensions.");
            }
            long length = checked((long)Width * Height * 4);
            if (length > 128 * 1024 * 1024)
            {
                throw new InvalidDataException("IPC frame exceeds the buffer limit.");
            }
            return length;
        }
    }
}

// Frame-ready/ack travels over the pipe; the producer must wait for ack before reusing these BGRA bytes.
internal sealed class BrowserIpcFrameBuffer : IDisposable
{
    private readonly string? _ownedDirectory;
    private readonly FileStream _file;
    internal BrowserIpcFrameDescriptor Descriptor { get; }
    internal string DirectoryPath { get; }
    internal MemoryMappedViewAccessor View { get; }
    private readonly MemoryMappedFile _mapping;
    private readonly Action<BrowserIpcFrameBuffer>? _released;
    private int _disposed;

    private BrowserIpcFrameBuffer(string directory, BrowserIpcFrameDescriptor descriptor, bool create, Action<BrowserIpcFrameBuffer>? released)
    {
        directory = BrowserIpcStorage.ValidateDirectory(directory);
        Descriptor = descriptor;
        DirectoryPath = directory;
        long length = descriptor.ByteLength;
        _ownedDirectory = create ? directory : null;
        _released = released;
        string path = Path.Combine(directory, descriptor.BufferId.ToString("N") + ".bgra");
        if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new UnauthorizedAccessException("IPC buffers cannot be links or reparse points.");
        }
        if (create == false)
        {
            BrowserIpcStorage.ValidateExistingFile(path);
        }
        var options = new FileStreamOptions
        {
            Mode = create ? FileMode.CreateNew : FileMode.Open,
            Access = create ? FileAccess.ReadWrite : FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = create ? FileOptions.DeleteOnClose : FileOptions.None
        };
        if (!OperatingSystem.IsWindows())
        {
            if (create)
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
        }
        _file = new FileStream(path, options);
        try
        {
            BrowserIpcStorage.ValidateFile(path, _file);
            if (OperatingSystem.IsLinux())
            {
                _LockLinux(_file, exclusive: false);
            }
            if (create)
            {
                _file.SetLength(length);
            }
            if (_file.Length != length)
            {
                throw new InvalidDataException("IPC frame buffer size mismatch.");
            }
            var access = create ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
            _mapping = MemoryMappedFile.CreateFromFile(_file, null, length, access,
                HandleInheritability.None, leaveOpen: true);
            try
            {
                View = _mapping.CreateViewAccessor(0, length, access);
            }
            catch
            {
                _mapping.Dispose();
                throw;
            }
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    internal static BrowserIpcFrameBuffer Create(string trustedParent, Guid generation, int width, int height,
        Action<BrowserIpcFrameBuffer>? released = null)
    {
        var descriptor = new BrowserIpcFrameDescriptor(generation, Guid.NewGuid(), width, height);
        _ = descriptor.ByteLength;
        trustedParent = BrowserIpcStorage.ValidateDirectory(trustedParent);
        CleanupAbandoned(trustedParent);
        string directory = Path.Combine(trustedParent, $"browser-{generation:N}-{descriptor.BufferId:N}");
        if (OperatingSystem.IsWindows())
        {
            _CreateWindowsDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try
        {
            return new BrowserIpcFrameBuffer(directory, descriptor, true, released);
        }
        catch
        {
            var errors = new List<Exception>();
            BrowserIpcCleanup.Try(() => File.Delete(Path.Combine(directory, descriptor.BufferId.ToString("N") + ".bgra")), errors);
            BrowserIpcCleanup.Try(() => Directory.Delete(directory), errors);
            throw;
        }
    }

    internal static BrowserIpcFrameBuffer Open(string trustedParent, Guid generation, BrowserIpcFrameDescriptor descriptor,
        Action<BrowserIpcFrameBuffer>? released = null)
    {
        if (descriptor.Generation != generation)
        {
            throw new InvalidDataException("Stale IPC frame buffer generation.");
        }
        trustedParent = BrowserIpcStorage.ValidateDirectory(trustedParent);
        string directory = Path.Combine(trustedParent, $"browser-{generation:N}-{descriptor.BufferId:N}");
        return new BrowserIpcFrameBuffer(directory, descriptor, false, released);
    }

    internal static void CleanupAbandoned(string trustedParent)
    {
        if (Directory.Exists(trustedParent) == false)
        {
            return;
        }
        trustedParent = BrowserIpcStorage.ValidateDirectory(trustedParent);
        foreach (string directory in Directory.EnumerateDirectories(trustedParent, "browser-*"))
        {
            string name = Path.GetFileName(directory);
            if (name.Length != 73 || name[40] != '-' || Guid.TryParseExact(name.AsSpan(8, 32), "N", out _) == false ||
                Guid.TryParseExact(name.AsSpan(41, 32), "N", out var id) == false)
            {
                continue;
            }
            try
            {
                if (BrowserIpcStorage.ValidateDirectory(directory) != directory)
                {
                    continue;
                }
                string path = Path.Combine(directory, id.ToString("N") + ".bgra");
                if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                {
                    continue;
                }
                BrowserIpcStorage.ValidateExistingFile(path);
                using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
                {
                    BrowserIpcStorage.ValidateFile(path, file);
                    if (OperatingSystem.IsLinux())
                    {
                        _LockLinux(file, exclusive: true);
                    }
                    File.Delete(path);
                }
                Directory.Delete(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A live buffer or an inaccessible leftover is never grounds for deleting another generation.
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static void _LockLinux(FileStream file, bool exclusive)
    {
        const int sharedLock = 1;
        const int exclusiveLock = 2;
        const int nonBlocking = 4;
        // flock belongs to the open description, so separate handles in this same process also exclude cleanup.
        if (_Flock(file.SafeFileHandle, (exclusive ? exclusiveLock : sharedLock) | nonBlocking) != 0)
        {
            throw new IOException("IPC frame buffer is in use.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int _Flock(SafeFileHandle handle, int operation);

    [SupportedOSPlatform("windows")]
    private static void _CreateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("The IPC buffer owner is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        var errors = new List<Exception>();
        BrowserIpcCleanup.Try(View.Dispose, errors);
        BrowserIpcCleanup.Try(_mapping.Dispose, errors);
        BrowserIpcCleanup.Try(_file.Dispose, errors);
        if (_ownedDirectory is { } directory)
        {
            BrowserIpcCleanup.Try(() => File.Delete(Path.Combine(directory, Descriptor.BufferId.ToString("N") + ".bgra")), errors);
            BrowserIpcCleanup.Try(() => Directory.Delete(directory), errors);
        }
        BrowserIpcCleanup.Try(() => _released?.Invoke(this), errors);
        if (errors.Count != 0)
        {
            throw new AggregateException("IPC buffer cleanup failed.", errors);
        }
    }
}
