using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Cockpit.Core.Configuration;

namespace Cockpit.Core.Sessions;

public static class SessionScratch
{
    public const string BaseVariable = "COCKPIT_SCRATCH_ROOT";
    public const string MarkerName = ".cockpit-owner";
    public static string BasePath => Path.GetFullPath(Environment.GetEnvironmentVariable(BaseVariable)
        ?? (OperatingSystem.IsWindows() ? @"C:\ck" : Path.Combine(Path.GetTempPath(), "ck")));
    public static string FallbackBase => Path.Combine(Path.GetTempPath(), "ck");
    public static string LegacyRoot => Path.Combine(Path.GetTempPath(), "cockpit", _Fingerprint());

    public static string PathFor(string paneId) => Path.Combine(_ResolveBase(null), _Name(paneId));

    public static string Create(string paneId, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        var path = Path.Combine(_ResolveBase(log), _Name(paneId));
        var marker = Path.Combine(path, MarkerName);
        if (Directory.Exists(path))
        {
            if (OwnerOf(path) != paneId)
            {
                throw new IOException($"Scratch directory '{path}' is not owned by this session.");
            }
            return path;
        }

        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new IOException("No Windows owner identity.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(_Fingerprint() + "\n" + paneId));
        return path;
    }

    private static string _ResolveBase(Action<string>? log)
    {
        var basis = BasePath;
        try
        {
            _Probe(basis);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            basis = FallbackBase;
            log?.Invoke($"Scratch base is not writable; using '{basis}': {exception.Message}");
            _Probe(basis);
        }

        return basis;
    }

    public static string Instruction(string path) =>
        $"Je tijdelijke map is $COCKPIT_SCRATCH ({path}). Zet tijdelijke bestanden, scripts, renders en clones alleen daar of in je worktree, nooit in de home-map of de root van een schijf. Cockpit ruimt die map op als je sessie sluit; wat bewaard moet blijven, hoort in de repo of in Depot.";

    public static string? OwnerOf(string path)
    {
        if (!Directory.Exists(path) || _IsLink(path) || _IsLink(Path.GetDirectoryName(Path.GetFullPath(path)) ?? path))
        {
            return null;
        }
        var marker = Path.Combine(path, MarkerName);
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }
        var parts = File.ReadAllText(marker).Split('\n');
        return parts.Length == 2 && parts[0] == _Fingerprint() && !string.IsNullOrWhiteSpace(parts[1])
            ? parts[1] : null;
    }

    public static void Delete(string path, string paneId, IEnumerable<string> registeredWorktrees)
    {
        if (OwnerOf(path) != paneId || registeredWorktrees.Any(worktree => IsInside(worktree, path)))
        {
            return;
        }
        _DeleteContents(path);
        File.Delete(Path.Combine(path, MarkerName));
        Directory.Delete(path);
    }

    public static bool IsInside(string path, string root) =>
        Path.GetFullPath(path).Equals(Path.GetFullPath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void _DeleteContents(string path, bool isRoot = true)
    {
        if (_IsLink(path))
        {
            throw new IOException("Scratch directory was replaced by a symbolic link or junction.");
        }
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (isRoot && entry.Name == MarkerName)
            {
                continue;
            }
            if ((entry.Attributes & FileAttributes.Directory) != 0 && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                _DeleteContents(entry.FullName, isRoot: false);
            }
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
            entry.Delete();
        }
    }

    private static void _Probe(string basis)
    {
        Directory.CreateDirectory(basis);
        if (_IsLink(basis))
        {
            throw new IOException("Scratch base contains a symbolic link or junction.");
        }
        using var probe = new FileStream(Path.Combine(basis, Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    private static bool _IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string _Name(string paneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        if (paneId.Contains('\n') || paneId.Contains('\r'))
        {
            throw new ArgumentException("A pane id must be a single line.", nameof(paneId));
        }
        return _Hash(_Fingerprint() + paneId)[..6];
    }

    private static string _Fingerprint()
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(CockpitBuild.StateRoot));
        return _Hash(OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized)[..16];
    }

    private static string _Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
