using Cockpit.Core.Projects;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Infrastructure.Assistant;

public static class ReplyAttachmentPath
{
    internal static string OpenedPath(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new IOException("The opened attachment path could not be verified.");
            }

            var path = buffer.ToString();
            return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }

        if (OperatingSystem.IsLinux())
        {
            return File.ResolveLinkTarget($"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle()}", true)?.FullName
                ?? throw new IOException("The opened attachment path could not be verified.");
        }

        if (OperatingSystem.IsMacOS())
        {
            var buffer = new StringBuilder(1024);
            if (fcntl(stream.SafeFileHandle, 50, buffer) == 0)
            {
                return buffer.ToString();
            }

            throw new IOException("The opened attachment path could not be verified.");
        }

        throw new NotSupportedException("Reply attachment path verification is unavailable on this platform.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle handle, int command, StringBuilder path);

    public static bool TryResolve(string path, IReadOnlyList<string> roots, out string resolved, out string? refusal)
    {
        resolved = string.Empty;
        refusal = "Only paths in the assistant scratch directory or managed worktrees may be attached; use operatorImage to select an image from the current turn.";
        try
        {
            var candidate = Path.GetFullPath(path);
            if (Path.GetFileName(candidate).Equals("cockpit.json", StringComparison.OrdinalIgnoreCase))
            {
                refusal = "cockpit.json may never be attached.";
                return false;
            }

            foreach (var root in roots)
            {
                var linkedRoot = false;
                for (var ancestor = new DirectoryInfo(Path.GetFullPath(root)); ancestor is not null; ancestor = ancestor.Parent)
                {
                    if (ancestor.LinkTarget is not null)
                    {
                        linkedRoot = true;
                        break;
                    }
                }

                if (linkedRoot)
                {
                    continue;
                }

                if (ProjectRootPath.TryResolve(root, Path.GetRelativePath(Path.GetFullPath(root), candidate), out var safe, out _)
                    && !Path.GetFileName(safe).Equals("cockpit.json", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = safe;
                    refusal = null;
                    return true;
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            refusal = "The attachment path could not be resolved safely: " + error.Message;
        }

        return false;
    }
}
