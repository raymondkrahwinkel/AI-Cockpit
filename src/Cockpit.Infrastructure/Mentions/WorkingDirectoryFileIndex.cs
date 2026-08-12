using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Mentions;

// AC-740's real @-mention file source: `git ls-files` first (gitignore respect for free), enumerate-fallback
// with a skiplist outside a repository. One cached snapshot per working directory (short TTL) behind a
// `Lazy<Task<...>>`, so concurrent opens on the same directory share one build instead of stampeding it.
internal sealed class WorkingDirectoryFileIndex : IMentionFileSource, ISingletonService
{
    // ponytail: matches Claude Code's own approximate index lifetime for this feature (its own cache invalidates
    // on a signature change, not a timer, but a 30s TTL is the cheap approximation of "stays fresh enough while
    // someone is actively mentioning files" without tracking filesystem-change signatures ourselves).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private const int EnumerationCap = 20_000;

    private static readonly HashSet<string> _SkippedDirectoryNames =
        new(StringComparer.Ordinal) { ".git", ".svn", ".hg", ".jj", "node_modules", "bin", "obj", ".venv", "venv", "__pycache__" };

    private readonly ConcurrentDictionary<string, _CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<string>> GetPathsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var entry = _cache.AddOrUpdate(
            workingDirectory,
            static (key, ttl) => _CacheEntry.Fresh(key, ttl),
            static (key, existing, ttl) => existing.IsExpired ? _CacheEntry.Fresh(key, ttl) : existing,
            CacheTtl);

        try
        {
            return await entry.Paths.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A failed build must not poison the cache for the rest of the TTL window — remove exactly the entry
            // this call created/found (not a newer one another caller may already have replaced it with), so the
            // next '@' retries instead of replaying the same failure.
            _cache.TryRemove(new KeyValuePair<string, _CacheEntry>(workingDirectory, entry));
            throw;
        }
    }

    private sealed class _CacheEntry(Lazy<Task<IReadOnlyList<string>>> paths, DateTimeOffset expiresAt)
    {
        public Lazy<Task<IReadOnlyList<string>>> Paths { get; } = paths;

        public bool IsExpired => DateTimeOffset.UtcNow >= expiresAt;

        public static _CacheEntry Fresh(string workingDirectory, TimeSpan ttl) =>
            new(new Lazy<Task<IReadOnlyList<string>>>(() => _BuildAsync(workingDirectory)), DateTimeOffset.UtcNow + ttl);
    }

    // Not tied to any individual caller's token: the build is shared across every caller that lands on this
    // entry, so one caller cancelling must not take the fetch down for the others still waiting on it.
    private static async Task<IReadOnlyList<string>> _BuildAsync(string workingDirectory)
    {
        var files = await _GitFilesAsync(workingDirectory).ConfigureAwait(false) ?? _EnumerateFiles(workingDirectory);

        var directories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var slash = file.LastIndexOf('/');
            while (slash > 0)
            {
                var directory = file[..slash];
                if (!directories.Add(directory))
                {
                    // Every ancestor of a directory already in the set got added when it was — nothing new up here.
                    break;
                }

                slash = directory.LastIndexOf('/');
            }
        }

        var paths = new List<string>(files.Count + directories.Count);
        paths.AddRange(files);
        paths.AddRange(directories.Select(directory => directory + "/"));
        return paths;
    }

    // git ls-files -z for tracked + untracked-not-ignored paths, '/'-separated by git itself. Null (not empty) on
    // anything short of a clean success, so the caller falls back to enumeration instead of reporting "no files"
    // for a git that is missing or a directory that isn't a repository.
    private static async Task<List<string>?> _GitFilesAsync(string workingDirectory)
    {
        GitResult tracked;
        try
        {
            tracked = await GitCli.RunAsync(workingDirectory, ["ls-files", "-z", "--recurse-submodules"], CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (tracked.ExitCode != 0)
        {
            return null;
        }

        var files = new List<string>();
        files.AddRange(tracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));

        var untracked = await GitCli.RunAsync(workingDirectory, ["ls-files", "-z", "--others", "--exclude-standard"], CancellationToken.None).ConfigureAwait(false);
        if (untracked.ExitCode == 0)
        {
            files.AddRange(untracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }

        return files;
    }

    // Outside a repository (or without git on PATH): a breadth-first walk with a skiplist for the usual
    // dependency/build directories and a hard cap so a huge non-git tree cannot make the first '@' unusable.
    // Reparse points (symlinks) are never followed — same as the git path, which never crosses one either.
    private static List<string> _EnumerateFiles(string workingDirectory)
    {
        var root = workingDirectory.TrimEnd('/', '\\');
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && files.Count < EnumerationCap)
        {
            var directory = pending.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (files.Count >= EnumerationCap)
                {
                    break;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!_SkippedDirectoryNames.Contains(Path.GetFileName(entry)))
                    {
                        pending.Push(entry);
                    }
                }
                else
                {
                    files.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                }
            }
        }

        return files;
    }
}
