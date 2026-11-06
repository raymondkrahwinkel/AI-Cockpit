using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

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
    private int _disposed;

    private BrowserIpcFrameBuffer(string directory, BrowserIpcFrameDescriptor descriptor, bool create)
    {
        Descriptor = descriptor;
        DirectoryPath = directory;
        long length = descriptor.ByteLength;
        _ownedDirectory = create ? directory : null;
        string path = Path.Combine(directory, descriptor.BufferId.ToString("N") + ".bgra");
        var options = new FileStreamOptions
        {
            Mode = create ? FileMode.CreateNew : FileMode.Open,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite | FileShare.Delete
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
            if (create)
            {
                _file.SetLength(length);
            }
            if (_file.Length != length)
            {
                throw new InvalidDataException("IPC frame buffer size mismatch.");
            }
            _mapping = MemoryMappedFile.CreateFromFile(_file, null, length, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, leaveOpen: true);
            try
            {
                View = _mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
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

    internal static BrowserIpcFrameBuffer Create(string trustedParent, Guid generation, int width, int height)
    {
        var descriptor = new BrowserIpcFrameDescriptor(generation, Guid.NewGuid(), width, height);
        _ = descriptor.ByteLength;
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
            return new BrowserIpcFrameBuffer(directory, descriptor, true);
        }
        catch
        {
            File.Delete(Path.Combine(directory, descriptor.BufferId.ToString("N") + ".bgra"));
            Directory.Delete(directory);
            throw;
        }
    }

    internal static BrowserIpcFrameBuffer Open(string trustedParent, Guid generation, BrowserIpcFrameDescriptor descriptor)
    {
        if (descriptor.Generation != generation)
        {
            throw new InvalidDataException("Stale IPC frame buffer generation.");
        }
        string directory = Path.Combine(trustedParent, $"browser-{generation:N}-{descriptor.BufferId:N}");
        return new BrowserIpcFrameBuffer(directory, descriptor, false);
    }

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
        View.Dispose();
        _mapping.Dispose();
        _file.Dispose();
        if (_ownedDirectory is { } directory)
        {
            File.Delete(Path.Combine(directory, Descriptor.BufferId.ToString("N") + ".bgra"));
            Directory.Delete(directory);
        }
    }
}
