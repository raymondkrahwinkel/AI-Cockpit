namespace Cockpit.Core.WorkingPaths;

// The remembered working directories offered in the New-session dialog (so a project folder you have used
// before is one click away instead of retyped): a most-recent-first `Recent` list, auto-capped,
// and a user-pinned `Favorites` list. Immutable — the `With…` helpers return a new instance
// with the change applied, and the store persists the result.
public sealed record WorkingPathHistory(IReadOnlyList<string> Recent, IReadOnlyList<string> Favorites)
{
    // How many recent paths are kept; older ones fall off the end. Favorites are separate and uncapped.
    public const int MaxRecent = 5;

    public static WorkingPathHistory Empty { get; } = new([], []);

    // Records `path` as the most-recently-used: moved (or added) to the front, de-duplicated
    // case-insensitively against the rest, and the list trimmed to `MaxRecent`. A blank path is
    // ignored (returns this unchanged). Favorites are untouched.
    public WorkingPathHistory WithRecent(string? path)
    {
        var trimmed = path?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return this;
        }

        var recent = new List<string> { trimmed };
        recent.AddRange(Recent.Where(existing => !_SamePath(existing, trimmed)));
        return this with { Recent = recent.Take(MaxRecent).ToList() };
    }

    // Pins (`favorite` true) or unpins `path`. Pinning de-duplicates; a blank path is ignored.
    public WorkingPathHistory WithFavorite(string? path, bool favorite)
    {
        var trimmed = path?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return this;
        }

        var favorites = Favorites.Where(existing => !_SamePath(existing, trimmed)).ToList();
        if (favorite)
        {
            favorites.Insert(0, trimmed);
        }

        return this with { Favorites = favorites };
    }

    // Removes `path` from the remembered directories entirely — dropped from both
    // `Recent` and `Favorites` (AC-131), so the New-session quick-pick's ✕ forgets it in one
    // go regardless of which list it was in. A blank path is ignored (returns this unchanged).
    public WorkingPathHistory WithoutPath(string? path)
    {
        var trimmed = path?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return this;
        }

        return this with
        {
            Recent = Recent.Where(existing => !_SamePath(existing, trimmed)).ToList(),
            Favorites = Favorites.Where(existing => !_SamePath(existing, trimmed)).ToList(),
        };
    }

    // True when `path` is currently pinned.
    public bool IsFavorite(string? path)
    {
        var trimmed = path?.Trim();
        return !string.IsNullOrEmpty(trimmed) && Favorites.Any(existing => _SamePath(existing, trimmed));
    }

    // Paths compare case-insensitively with trailing separators ignored, so "C:\Proj" and "C:\Proj\" are one
    // entry — matches how Windows treats them and keeps the list free of near-duplicates.
    private static bool _SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
}
