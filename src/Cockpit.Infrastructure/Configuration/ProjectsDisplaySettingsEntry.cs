using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `ProjectsDisplaySettings` in the `projects` section of `cockpit.json`.
internal sealed class ProjectsDisplaySettingsEntry
{
    // Stored by name rather than by number, so a value written by a build that knows one more layout than this one
    // reads back as a name this one does not recognise — and falls to the default — instead of silently meaning a
    // different layout.
    public string? LayoutMode { get; set; }

    public static ProjectsDisplaySettingsEntry FromDomain(ProjectsDisplaySettings settings) => new()
    {
        LayoutMode = settings.LayoutMode.ToString(),
    };

    public ProjectsDisplaySettings ToDomain() => new()
    {
        LayoutMode = Enum.TryParse<ProjectsLayoutMode>(LayoutMode, ignoreCase: true, out var mode)
            ? mode
            : ProjectsLayoutMode.Cards,
    };
}
