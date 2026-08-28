namespace Cockpit.Infrastructure.Delegation;

// AC-1160: a path spelled the way the filesystem itself spells it, so a security boundary compares what the
// volume underneath resolves rather than what the running operating system usually does.

// One syscall per path segment, deliberately: this runs once per delegation check and sits on no hot loop.
// The canonicalisation is worth more than the calls it costs -- do not trade it back for a string comparison.
internal static class FilesystemPath
{
    // Where the OS gives up on a link chain too. Exhausting it yields no path at all rather than the partly
    // resolved one: forty links that stay inside an allowed root and a forty-first that leaves it would
    // otherwise be judged on the spelling after hop forty, while the OS follows the chain all the way out.
    private const int MaxLinkHops = 40;

    // Hidden and system directories are ordinary directories to this question, and the default options skip
    // both -- leaving a hidden segment unresolvable and its path strictly compared for no reason.
    private static readonly EnumerationOptions _AnyEntryIgnoringCase = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.None,
        ReturnSpecialDirectories = false,
    };

    // Case folding belongs to the volume, not to the platform: a case-insensitive mount on Linux and a
    // case-sensitive volume on macOS both exist, so `RuntimeInformation` cannot answer this. Every segment is
    // resolved against its own directory entry instead, and every link followed, by asking the filesystem.

    // A segment with nothing behind it -- a path that does not exist, a directory that cannot be read -- keeps
    // the spelling it was given, so two such paths match only when spelled identically. Null is the harder no:
    // the link chain never came to rest, and the caller must refuse rather than judge a half-resolved path.

    // `maxLinkHops` is a test seam. Forty mid-path links are awkward to lay down and cheap to simulate.
    internal static string? Canonicalize(string path, int maxLinkHops = MaxLinkHops)
    {
        var current = _Rooted(path);

        for (var hop = 0; hop < maxLinkHops; hop++)
        {
            var (walked, followedLink) = _Walk(current);
            if (!followedLink)
            {
                return walked;
            }

            // Re-rooted because a link target is whatever the link says: it may be relative, and it may carry
            // its own `..` and its own casing, all of which get the same treatment on the next pass.
            current = _Rooted(walked);
        }

        return null;
    }

    private static string _Rooted(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Left to right, replacing each segment with the entry the filesystem resolves it to, and stopping at the
    // first link so the caller can restart from its target with the remaining segments appended. That is what
    // gets a link halfway up a path resolved: .NET only ever resolves one that is the final segment.
    private static (string Path, bool FollowedLink) _Walk(string path)
    {
        if (Path.GetPathRoot(path) is not { Length: > 0 } root)
        {
            return (path, false);
        }

        var segments = path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = _ResolveSegment(current, segments[index]);

            if (_LinkTarget(current) is not { } target)
            {
                continue;
            }

            var remaining = segments[(index + 1)..];
            return (remaining.Length == 0 ? target : Path.Combine([target, .. remaining]), true);
        }

        return (current, false);
    }

    // An entry spelled exactly as asked is its own directory, even where a sibling differing only in case also
    // exists -- that pair is the whole point on a case-sensitive volume. Only when no exactly-spelled entry
    // exists and the filesystem still resolves the path has the volume folded the case onto its one match.
    private static string _ResolveSegment(string parent, string segment)
    {
        var combined = Path.Combine(parent, segment);

        // `*` and `?` are the only wildcards the simple match type reads, and a segment carrying one would ask
        // the filesystem a different question than the one being answered here.
        if (segment.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            return combined;
        }

        try
        {
            var matches = Directory.GetFileSystemEntries(parent, segment, _AnyEntryIgnoringCase);
            var exact = matches.FirstOrDefault(
                match => string.Equals(Path.GetFileName(match), segment, StringComparison.Ordinal));

            return exact ?? (matches.Length == 1 && Directory.Exists(combined) ? matches[0] : combined);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return combined;
        }
    }

    private static string? _LinkTarget(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                ?? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
