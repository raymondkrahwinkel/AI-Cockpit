using System.Globalization;
using Avalonia.Data.Converters;
using Cockpit.Core.Projects;

namespace Cockpit.App.Converters;

// AC-1529: a session's `ProjectId` to the name the Simple header shows, or null for a session outside any project.
// AC-1528: given the working directory as a third value, a session outside any project is named by its folder.
public sealed class ProjectNameConverter : IMultiValueConverter
{
    public static readonly ProjectNameConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = (values is [string id, IEnumerable<Project> projects, ..] ? projects.FirstOrDefault(project => project.Id == id)?.Name : null)
            ?? (values is [_, _, string { Length: > 0 } folder] ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) : null);

        // A format as parameter ("in {0}") wraps the name only, so nothing at all stands where there is none.
        return name is not null && parameter is string format ? string.Format(culture, format, name) : name;
    }
}
