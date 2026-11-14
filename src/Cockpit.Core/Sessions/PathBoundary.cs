namespace Cockpit.Core.Sessions;

public static class PathBoundary
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static PathBoundaryResult Evaluate(string path, IEnumerable<string> roots)
    {
        try
        {
            var full = Path.GetFullPath(path);
            foreach (var root in roots)
            {
                var normalizedRoot = Path.GetFullPath(root);
                if (_IsUnder(full, normalizedRoot) && !_LinksOutside(full, normalizedRoot))
                {
                    return new(true);
                }
            }

            return new(false, "The path or a link target is outside the allowed roots.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return new(false, "The path could not be resolved safely.");
        }
    }

    private static bool _IsUnder(string path, string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var prefix = Path.EndsInDirectorySeparator(trimmed) ? trimmed : trimmed + Path.DirectorySeparatorChar;
        return path.Equals(trimmed, Comparison) || path.StartsWith(prefix, Comparison);
    }

    private static bool _LinksOutside(string path, string root, int remainingLinks = 40)
    {
        if (remainingLinks == 0)
        {
            return true;
        }
        var rootLength = Path.TrimEndingDirectorySeparator(root).Length;
        for (var current = path; current is not null && current.Length > rootLength; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not { } link)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(link, Path.GetDirectoryName(current) ?? root);
            if (!_IsUnder(target, root) || _LinksOutside(target, root, remainingLinks - 1))
            {
                return true;
            }
        }

        return false;
    }
}
