using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// What a plugin's settings dialog puts under its Save/Close footer (`Content`), and whether it drew
// a navigation rail (`HasRail`), so the dialog owes it the width.
internal readonly record struct PluginSettingsBody(Control Content, bool HasRail);

// Builds the body of a plugin's settings dialog: its view in the host's scroll and inset, with the Options
// rail when it declares sections (`IPluginSettingsSections`, AC-316). Separate from `PluginDialogHost` so
// it can be built and asserted without a running app.
internal static class PluginSettingsBodyBuilder
{
    // How wide a dialog opens once it has gained a rail: settings keep their room, but never past `maximum`
    // (the cockpit's own cap); on a too-narrow cockpit the cap wins and the rail eats into settings' width.
    internal static (double Width, double MinWidth) GrowForRail(double width, double minWidth, double maximum, double railWidth) =>
        (Math.Min(width + railWidth, maximum), Math.Min(minWidth + railWidth, maximum));

    internal static PluginSettingsBody Build(Control view)
    {
        // A view that names two or more sections gets the rail; one section navigates nothing, and a view that
        // never heard of sections is the flat dialog every plugin has today.
        if (view is not IPluginSettingsSections sections || sections.SectionTitles.Count < 2)
        {
            return new PluginSettingsBody(_ScrolledView(view), HasRail: false);
        }

        return new PluginSettingsBody(_WithRail(view, sections), HasRail: true);
    }

    // Inset lives in a Border *inside* the scrolled content, not as ScrollViewer.Padding: Avalonia leaves a
    // ScrollViewer's own padding out of the scroll extent, so a tall view could not scroll its last ~24px clear.
    private static ScrollViewer _ScrolledView(Control view) =>
        new() { Content = new Border { Padding = new Thickness(14, 12), Child = view } };

    // The same rail the Options dialog grew in AC-69, over the same Theme.axaml styles, so both read as one
    // thing. The view stays attached for the whole dialog, so attach/detach-driven behavior (loading profiles,
    // dropping subscriptions) works exactly as it does without a rail.
    private static Control _WithRail(Control view, IPluginSettingsSections sections)
    {
        var scroll = _ScrolledView(view);
        var rail = new ListBox { Classes = { "subnav" }, ItemsSource = sections.SectionTitles };
        rail.SelectionChanged += (_, _) =>
        {
            if (rail.SelectedIndex < 0)
            {
                return;
            }

            sections.ShowSection(rail.SelectedIndex);

            // A ScrollViewer keeps its offset when its content is replaced, so without this a short section opens
            // scrolled to wherever the taller one before it had been left — reading as a page missing its top.
            scroll.Offset = default;
        };
        rail.SelectedIndex = 0;

        var railColumn = new Border
        {
            Classes = { "subnavRail" },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Classes = { "subnavGroup" }, Text = "SETTINGS" },
                    rail,
                },
            },
        };
        Grid.SetColumn(scroll, 1);

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        split.Children.Add(railColumn);
        split.Children.Add(scroll);
        return split;
    }
}
