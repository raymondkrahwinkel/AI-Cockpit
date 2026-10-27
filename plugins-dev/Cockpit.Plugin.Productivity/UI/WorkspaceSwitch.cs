using Avalonia.Controls;
using Avalonia.Layout;

namespace Cockpit.Plugin.Productivity.UI;

// The Uren | Overzicht switch both screens open with, so one click leads to the other (AC-1816).
internal static class WorkspaceSwitch
{
    public static Control Build(bool overviewActive, Action openHours, Action openOverview)
    {
        var hours = new Button { Name = "HoursTab", Content = "Uren", Classes = { "Compact" } };
        var overview = new Button { Name = "OverviewTab", Content = "Overzicht", Classes = { "Compact" } };
        (overviewActive ? overview : hours).Classes.Add("Accent");
        hours.Click += (_, _) =>
        {
            if (overviewActive)
            {
                openHours();
            }
        };
        overview.Click += (_, _) =>
        {
            if (overviewActive == false)
            {
                openOverview();
            }
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { hours, overview } };
    }
}
