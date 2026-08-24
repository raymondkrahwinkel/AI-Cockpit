namespace Cockpit.Core.Projects;

// AC-1013: Which project answers to an identifier from somewhere else (AC-419) — reverse of Project.LinkedAs.
// Companion to ProjectDirectoryMatch for a plugin with no folder yet but a ticket naming a linked tracker project.
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
