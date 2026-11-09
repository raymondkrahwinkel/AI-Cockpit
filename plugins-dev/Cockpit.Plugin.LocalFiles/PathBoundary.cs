using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.LocalFiles;

// The path limit of a root (plan AC-1782 §5): every FileRef is resolved here, against the physical disk, and refused
// unless the result lies inside the root and outside every denied folder. A gap here opens the whole disk silently.
internal static class PathBoundary
{
    public const string TempMarker = ".cockpit-tmp-";

    private const int MaxLinkHops = 40;

    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Windows opens these as devices in any folder and with any extension; refused everywhere, so a root behaves the same on every OS.
    private static readonly HashSet<string> Devices = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
            .. Enumerable.Range(0, 10).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" }),
            .. new[] { "¹", "²", "³" }.SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
        StringComparer.OrdinalIgnoreCase);

    private static readonly char[] Refused = ['\\', ':', '*', '?', '"', '<', '>', '|', .. Path.GetInvalidFileNameChars()];

    // The physical path of `relative` under `root`, which is itself a physical path. "/" is the root.
    public static string Resolve(string root, string relative, IReadOnlyCollection<string> denied)
    {
        var segments = Segments(relative);
        var full = Path.GetFullPath(Path.Join(root, string.Join(Path.DirectorySeparatorChar, segments)));
        if (!IsWithin(root, full))
        {
            throw Outside();
        }
        var real = RealPath(full);
        if (!IsWithin(root, real) || denied.Any(folder => IsWithin(folder, real)))
        {
            throw Outside();
        }
        return real;
    }

    // A relative, '/'-separated path, split into names that mean the same file on every OS; anything else is refused.
    public static string[] Segments(string? relative)
    {
        if (relative == "/")
        {
            return [];
        }
        if (string.IsNullOrEmpty(relative) || relative[0] == '/')
        {
            throw Outside();
        }
        var segments = relative.Split('/');
        return segments.All(IsName) ? segments : throw Outside();
    }

    public static bool IsName(string name) =>
        name.Length > 0 && name is not "." and not ".." && name.IndexOfAny(Refused) < 0 && !name.Any(char.IsControl)
        && !name.EndsWith('.') && !name.EndsWith(' ') && !Devices.Contains(name.Split('.')[0].TrimEnd(' '))
        && !name.Contains(TempMarker, StringComparison.OrdinalIgnoreCase);

    public static bool IsWithin(string folder, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        return path.Equals(root, Comparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);
    }

    // Follows every link on the way, one name at a time from the drive root, so '..' and a relative target are taken
    // from where the link really is. On Windows every name is replaced by its long name, so an 8.3 alias cannot pass for another folder.
    public static string RealPath(string path)
    {
        var hops = 0;
        return _RealPath(path, ref hops);
    }

    private static string _RealPath(string path, ref int hops)
    {
        var current = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(current))
        {
            throw Outside();
        }
        var names = path[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names)
        {
            if (name == ".")
            {
                continue;
            }
            if (name == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }
            var next = Path.Join(current, name);
            var target = (Directory.Exists(next) ? new DirectoryInfo(next) : (FileSystemInfo)new FileInfo(next)).LinkTarget;
            if (target is null)
            {
                current = OperatingSystem.IsWindows() ? Path.Join(current, _LongName(current, name)) : next;
                continue;
            }
            if (++hops > MaxLinkHops)
            {
                throw Outside();
            }
            current = _RealPath(Path.IsPathFullyQualified(target) ? target : Path.Join(current, target), ref hops);
        }
        return current;
    }

    // Only a name of an existing item has a long form; a new file keeps the name it is given.
    private static string _LongName(string folder, string name) =>
        name.IndexOfAny(['*', '?']) < 0 && Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFileSystemInfos(name).FirstOrDefault()?.Name ?? name
            : name;

    public static FileSourceException Outside() =>
        new(FileSourceErrorKind.Forbidden, "Forbidden: this path leads outside the folder you added, or into Cockpit's own state folder.");
}
