using System.Globalization;

namespace Cockpit.Core.Updates;

/// <summary>
/// Whether a release is worth telling the operator about (#71). Two kinds of build, and they are compared by
/// different questions, because they answer to different things.
/// <para>
/// A <b>tagged release</b> has a version, so the question is "is it higher than mine". A <b>nightly</b> has no version
/// — it is a rolling tag that is overwritten every night — so the only honest question is "is it a different commit
/// than the one I was built from". That is why the nightly workflow puts the commit in the release, and why this
/// compares shas rather than pretending a date is a version.
/// </para>
/// <para>
/// Everything here refuses rather than guesses. A version this cockpit cannot read, a nightly with no commit, a build
/// that is the same as ours: nothing to announce. Telling someone there is an update when there is not is how a
/// notification becomes something people learn to dismiss.
/// </para>
/// </summary>
public static class UpdateComparison
{
    /// <summary>Whether <paramref name="release"/> is a build this cockpit does not already have.</summary>
    public static bool IsNewer(AppRelease release, string currentVersion, string currentCommit)
    {
        if (release.IsPrerelease)
        {
            // A nightly is identified by its commit and nothing else. No commit on either side means no honest
            // comparison, and no notification.
            var theirs = release.Commit.Trim();
            var ours = currentCommit.Trim();

            return theirs.Length > 0
                && ours.Length > 0
                && !theirs.StartsWith(ours, StringComparison.OrdinalIgnoreCase)
                && !ours.StartsWith(theirs, StringComparison.OrdinalIgnoreCase);
        }

        return Compare(release.Version, currentVersion) > 0;
    }

    /// <summary>
    /// Compares two versions: positive when <paramref name="candidate"/> is higher. A pre-release suffix loses to the
    /// same version without one (<c>1.2.0-nightly.4</c> is not <c>1.2.0</c>), which is what semver says and what an
    /// operator would expect. Anything unreadable compares as "not higher" — a build we cannot understand is not one
    /// we should push someone towards.
    /// </summary>
    public static int Compare(string? candidate, string? current)
    {
        if (_Parse(candidate) is not { } theirs || _Parse(current) is not { } ours)
        {
            return 0;
        }

        for (var index = 0; index < 3; index++)
        {
            if (theirs.Numbers[index] != ours.Numbers[index])
            {
                return theirs.Numbers[index].CompareTo(ours.Numbers[index]);
            }
        }

        // Same numbers: a release beats a pre-release of itself, and two pre-releases fall back on text.
        return (theirs.PreRelease.Length == 0, ours.PreRelease.Length == 0) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => -1,
            _ => string.Compare(theirs.PreRelease, ours.PreRelease, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static (int[] Numbers, string PreRelease)? _Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        // A tag is "v1.2.3"; the assembly's own informational version carries "+<sha>" build metadata.
        var text = version.Trim().TrimStart('v', 'V');
        var build = text.IndexOf('+');
        if (build >= 0)
        {
            text = text[..build];
        }

        var preRelease = string.Empty;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var parts = text.Split('.');
        if (parts.Length is 0 or > 3)
        {
            return null;
        }

        var numbers = new int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return null;
            }
        }

        return (numbers, preRelease);
    }
}
