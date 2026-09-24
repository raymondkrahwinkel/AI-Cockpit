namespace Cockpit.Core.Projects;

// Resolves a project-relative path and rejects paths or existing links that leave the root.
public static class ProjectRootPath
{
    public static bool TryResolve(string root, string relativePath, out string fullPath, out string? refusal)
    {
        fullPath = string.Empty;
        refusal = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            refusal = "A relative path is required.";
            return false;
        }

        if (Path.IsPathRooted(relativePath) || relativePath[0] is '/' or '\\' ||
            (relativePath.Length >= 2 && char.IsLetter(relativePath[0]) && relativePath[1] == ':'))
        {
            refusal = "The path must be relative to the project root.";
            return false;
        }

        if (OperatingSystem.IsWindows() && relativePath.Contains(':'))
        {
            refusal = "Alternate data streams are not allowed.";
            return false;
        }

        try
        {
            var rootFull = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var rootPrefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;

            if (!candidate.Equals(rootFull, comparison) && !candidate.StartsWith(rootPrefix, comparison))
            {
                refusal = "The path leaves the project root.";
                return false;
            }

            var current = rootFull;
            var segments = candidate.Equals(rootFull, comparison)
                ? Array.Empty<string>()
                : Path.GetRelativePath(rootFull, candidate).Split(Path.DirectorySeparatorChar);

            for (var index = -1; index < segments.Length; index++)
            {
                if (index >= 0)
                {
                    current = Path.Combine(current, segments[index]);
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }

                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    continue;
                }

                FileSystemInfo link = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                var target = link.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || (!target.FullName.Equals(rootFull, comparison) &&
                    !target.FullName.StartsWith(rootPrefix, comparison)))
                {
                    refusal = "A link leaves the project root or cannot be resolved.";
                    return false;
                }
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or
                   IOException or UnauthorizedAccessException)
        {
            refusal = "The path could not be resolved safely.";
            return false;
        }
    }
}
