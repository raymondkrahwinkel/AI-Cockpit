using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Delegation;

// What a delegated task changed on disk, told by the host rather than by the task (AC-971). A closing summary is
// exactly where an overstep goes missing — the fork that wrote 68 files mentioned none of them — so the host takes
// its own porcelain reading at start and at finish, and the difference rides with the result.
//
// A directory that is not a work tree yields null — "could not be established" — never an empty list, which would
// read as "changed nothing".
internal static class DelegatedWorkspaceChanges
{
    // A shorter guard than GitCli's default: this runs on the path that reports a finished task, and a wedged index
    // lock must not hold the result back for two minutes.
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);

    // The paths git currently reports as changed in `directory`, or null when it is not inside a work tree.
    public static async Task<IReadOnlySet<string>?> SnapshotAsync(string? directory, CancellationToken cancellationToken = default)
    {
        if (directory is not { Length: > 0 } || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            // -z so a path with a space or an accent comes back whole rather than quoted-and-escaped;
            // --untracked-files=all so a new directory is not collapsed to one entry ("src/" for forty files).
            var result = await GitCli.RunAsync(
                directory,
                ["status", "--porcelain", "-z", "--untracked-files=all"],
                cancellationToken,
                timeout: StatusTimeout).ConfigureAwait(false);

            return result.ExitCode == 0 ? ParsePorcelain(result.StandardOutput) : null;
        }
        catch (Exception)
        {
            // No git, no such directory, a timeout: unknowable, which is not the same as unchanged.
            return null;
        }
    }

    // The paths in `after` that were not already changed in `before`. A null `after` (not a work tree, or the
    // reading failed) yields null — the caller must be able to tell "nothing changed" from "cannot say".
    public static IReadOnlyList<string>? Added(IReadOnlySet<string>? before, IReadOnlySet<string>? after)
    {
        if (after is null)
        {
            return null;
        }

        var added = before is null ? after : after.Where(path => !before.Contains(path));
        return [.. added.OrderBy(path => path, StringComparer.Ordinal)];
    }

    // Splits `git status --porcelain -z` output into paths. Each record is `XY <path>`; a rename or copy (X is R or
    // C) is followed by a second NUL-terminated field holding the original path, which is a real path the task
    // touched too, so both sides are kept.
    public static IReadOnlySet<string> ParsePorcelain(string output)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var fields = output.Split('\0');

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length < 4)
            {
                continue;
            }

            paths.Add(field[3..]);

            // The origin of a rename/copy rides in the next field, which must not then be read as a record of its own.
            if ((field[0] is 'R' or 'C' || field[1] is 'R' or 'C') && index + 1 < fields.Length && fields[index + 1].Length > 0)
            {
                paths.Add(fields[++index]);
            }
        }

        return paths;
    }
}
