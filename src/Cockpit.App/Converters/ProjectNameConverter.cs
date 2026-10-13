using System.Globalization;
using Avalonia.Data.Converters;
using Cockpit.Core.Projects;

namespace Cockpit.App.Converters;

// AC-1529: a session's `ProjectId` to the name the Simple header shows, or null for a session outside any project.
public sealed class ProjectNameConverter : IMultiValueConverter
{
    public static readonly ProjectNameConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [string id, IEnumerable<Project> projects]
            ? projects.FirstOrDefault(project => project.Id == id)?.Name
            : null;
}
