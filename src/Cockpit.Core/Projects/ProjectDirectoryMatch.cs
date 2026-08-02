using Cockpit.Core.WorkingPaths;

namespace Cockpit.Core.Projects;

// Which project a folder belongs to (AC-320). A session started from the New-session routes is told its project
// by the operator; one a plugin embeds — an Autopilot step, a workflow run — is told a folder and nothing else,
// and this is how the host works out the rest.
//
// A project is identified by the folder it owns (`Project.SourceDirectory`), so the folder a session
// runs in is the honest answer to "which project is this". Deliberately not a fuzzy match: a run in a folder no
// project claims belongs to no project, rather than to whichever one looked closest.
public static class ProjectDirectoryMatch
{
    // The project `directory` belongs to, or `null` when none claims it.
    // A folder inside a project's source folder counts as that project's — a run pointed at `repo/src` works on
    // the same project as one pointed at `repo` — and the most specific claim wins, so a project nested inside
    // another's folder keeps its own sessions. Two projects claiming the *same* folder match neither: picking
    // one of them would decide by storage order which project's environment a run carries, and a run with no project
    // is a smaller wrong than a run with the wrong one.
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
            if (DirectoryPath.Normalize(project.SourceDirectory) is not { } source || !DirectoryPath.IsWithin(target, source))
            {
                continue;
            }

            if (source.Length > bestLength)
            {
                best = project;
                bestLength = source.Length;
                ambiguous = false;
            }
            else if (source.Length == bestLength)
            {
                // The same folder claimed twice: no answer beats an arbitrary one (see the remarks above). Keep
                // walking — a more specific claim further down the list still wins over both.
                ambiguous = true;
            }
        }

        return ambiguous ? null : best;
    }
}
