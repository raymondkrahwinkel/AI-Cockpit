using Cockpit.Core.WorkingPaths;

namespace Cockpit.Core.Projects;

// AC-1013: Which project a folder belongs to (AC-320), for callers told only a folder (an Autopilot step, a
// workflow run) and not the project. Deliberately not a fuzzy match: an unclaimed folder belongs to no project,
// rather than to whichever one looked closest.
public static class ProjectDirectoryMatch
{
    // AC-1013: The project `directory` belongs to, or null when none claims it. Most specific containing
    // source folder wins; two projects claiming the same folder match neither (a run with no project is a
    // smaller wrong than one with the wrong project).
    public static Project? For(IEnumerable<Project> projects, string? directory)
    {
        if (DirectoryPath.Normalize(directory) is not { } target)
        {
            return null;
        }

        Project? best = null;
        var bestLength = -1;
        var ambiguous = false;

        foreach (var project in projects)
        {
            // Every declared repository gets a claim, not only item 0 — a spread-out project (repositories not
            // nested in each other) needs a run in either one to match. Two of this project's own folders claiming
            // the target isn't cross-project ambiguity, so only the best claim per project feeds that check below.
            var bestOwn = -1;
            foreach (var repository in project.SourceDirectories)
            {
                if (DirectoryPath.Normalize(repository.Path) is { } source && DirectoryPath.IsWithin(target, source) && source.Length > bestOwn)
                {
                    bestOwn = source.Length;
                }
            }

            if (bestOwn < 0)
            {
                continue;
            }

            if (bestOwn > bestLength)
            {
                best = project;
                bestLength = bestOwn;
                ambiguous = false;
            }
            else if (bestOwn == bestLength)
            {
                // The same folder claimed twice by two different projects: no answer beats an arbitrary one (see
                // the remarks above). Keep walking — a more specific claim further down the list still wins over both.
                ambiguous = true;
            }
        }

        return ambiguous ? null : best;
    }
}
