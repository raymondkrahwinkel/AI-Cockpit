namespace Cockpit.Core.Projects;

// Which project answers to an identifier from somewhere else (AC-419) — the YouTrack project an issue lives in, the
// repository an issue is on. The reverse of `Project.LinkedAs`: that asks what one project is called over
// there, this asks which project is called that.
//
// The companion to `ProjectDirectoryMatch`, which places a session by the folder it runs in. A plugin
// opening the New-session dialog has no folder yet — the whole point is that the operator has not picked one — but it
// does know the ticket it is acting on, and that ticket names a tracker project the operator already linked.
public static class ProjectLinkMatch
{
    // The project linked as `value` under `fieldKey` — checked against every value the field names (AC-884, e.g.
    // `EWB, AT, EJ`) — or `null` when none matches, or when two projects' value sets overlap (ambiguous, same
    // reason `ProjectDirectoryMatch.For` refuses a shared folder). Comparison is case-insensitive throughout.
    public static Project? For(IEnumerable<Project> projects, string fieldKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(fieldKey) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Project? match = null;
        foreach (var project in projects)
        {
            if (!project.LinkedAsAll(fieldKey).Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = project;
        }

        return match;
    }
}
