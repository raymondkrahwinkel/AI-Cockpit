namespace Cockpit.Plugin.YouTrack;

// The path argument on `attach_message_images_to_issue` (AC-170) is a genuine outbound channel — an agent
// names a file and its bytes leave the machine — so it gets the same scrutiny as any other filesystem-facing
// tool input: an explicit allow-list, not a free-form path.
//
// Two roots are allowed: the terminal-paste spill directory (where Exclr8's paste handler writes clipboard
// images, see `TerminalControl.WriteClipboardBytesToTemp`/`WriteClipboardBitmapToTemp` — this plugin
// does not reference that third-party project, so the directory name is duplicated here rather than shared) and
// the calling session's own working directory (so an agent can attach a file it wrote itself). Everything else
// is refused.
//
// Containment is checked on a canonical absolute path, not a string prefix of the raw input: `Path.GetFullPath(string)` collapses `..` segments, and when the target is itself a reparse point
// (a symlink or junction), `File.ResolveLinkTarget` is followed to its real, final location before
// the containment check runs — so `exclr8-terminal-paste\link-out.png` pointing outside both roots is
// caught, not waved through because the link's own path looked fine.
internal static class AttachImagePathGuard
{
    // Directory name under the OS temp dir where terminal-pasted images are spilled — kept in sync with `TerminalControl.PasteImageDirectoryName`'s default.
    private const string PasteImageDirectoryName = "exclr8-terminal-paste";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Resolves `rawPath` to a canonical absolute path and checks it is genuinely contained in
    // one of the allowed roots (the paste directory, or `sessionWorkingDirectory` when known),
    // and that the file exists. Returns the resolved path on success; otherwise `null` with an
    // error message safe to hand back to the calling agent.
    public static bool TryResolve(string rawPath, string? sessionWorkingDirectory, out string resolvedPath, out string error)
    {
        resolvedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "No path was given.";
            return false;
        }

        string candidate;
        try
        {
            // GetFullPath collapses ".." / "." segments and makes the path absolute — the raw string is never
            // compared directly, only this canonical form.
            candidate = Path.GetFullPath(rawPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"\"{rawPath}\" is not a usable path ({exception.Message}).";
            return false;
        }

        if (!File.Exists(candidate))
        {
            error = $"No file exists at \"{rawPath}\".";
            return false;
        }

        // Follow a symlink/junction to where it actually points, so containment is checked against the real
        // file, not the link's own (possibly innocent-looking) location. A non-link file resolves to itself.
        var real = File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? candidate;

        var roots = _AllowedRoots(sessionWorkingDirectory);
        if (!roots.Any(root => _IsContainedIn(candidate, root) && _IsContainedIn(real, root)))
        {
            error = $"\"{rawPath}\" is outside the folders this tool may attach from (the terminal-paste folder, or this session's working directory). Refused.";
            return false;
        }

        resolvedPath = real;
        return true;
    }

    private static IReadOnlyList<string> _AllowedRoots(string? sessionWorkingDirectory)
    {
        var roots = new List<string> { Path.Combine(Path.GetTempPath(), PasteImageDirectoryName) };
        if (!string.IsNullOrWhiteSpace(sessionWorkingDirectory))
        {
            roots.Add(sessionWorkingDirectory);
        }

        return roots;
    }

    private static bool _IsContainedIn(string path, string root)
    {
        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // A trailing separator on the root means "C:\Allowed2\x" cannot pass a bare "C:\Allowed" prefix check.
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, PathComparison) || string.Equals(path, rootFull, PathComparison);
    }
}
