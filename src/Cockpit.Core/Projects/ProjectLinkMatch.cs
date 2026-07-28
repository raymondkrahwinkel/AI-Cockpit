namespace Cockpit.Core.Projects;

/// <summary>
/// Which project answers to an identifier from somewhere else (AC-419) — the YouTrack project an issue lives in, the
/// repository an issue is on. The reverse of <see cref="Project.LinkedAs"/>: that asks what one project is called over
/// there, this asks which project is called that.
/// <para>
/// The companion to <see cref="ProjectDirectoryMatch"/>, which places a session by the folder it runs in. A plugin
/// opening the New-session dialog has no folder yet — the whole point is that the operator has not picked one — but it
/// does know the ticket it is acting on, and that ticket names a tracker project the operator already linked.
/// </para>
/// </summary>
public static class ProjectLinkMatch
{
    /// <summary>
    /// The project linked as <paramref name="value"/> under <paramref name="fieldKey"/>, or <see langword="null"/> when
    /// none is — or when more than one is.
    /// </summary>
    /// <remarks>
    /// Two projects carrying the same link match neither, for the reason <see cref="ProjectDirectoryMatch.For"/>
    /// refuses a folder two projects claim: choosing one of them decides by storage order, and a preselection the
    /// operator trusts is worse when it is wrong than no preselection at all. Values compare case-insensitively —
    /// a tracker short name and an owner/repo are not case-sensitive identifiers, so two projects linked to
    /// <c>AC</c> and <c>ac</c> are the same link twice and count as the ambiguity they are.
    /// </remarks>
    public static Project? For(IEnumerable<Project> projects, string fieldKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(fieldKey) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Project? match = null;
        foreach (var project in projects)
        {
            if (!string.Equals(project.LinkedAs(fieldKey), value, StringComparison.OrdinalIgnoreCase))
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
