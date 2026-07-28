using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>What a plugin's settings dialog puts under its Save/Close footer, and whether it drew a rail to do it.</summary>
/// <param name="Content">The control the dialog hosts.</param>
/// <param name="HasRail">True when a navigation rail was drawn, so the dialog owes it the width.</param>
internal readonly record struct PluginSettingsBody(Control Content, bool HasRail);

/// <summary>
/// Builds the body of a plugin's settings dialog: the plugin's view in the host's scroll and inset, with the
/// Options navigation rail beside it when the view declares sections (<see cref="IPluginSettingsSections"/>,
/// AC-316). Separate from <see cref="PluginDialogHost"/> because it is the part that can be built and asserted
/// on without a window — opening the dialog itself needs a running app.
/// </summary>
internal static class PluginSettingsBodyBuilder
{
    /// <summary>
    /// How wide a dialog opens once it has gained a rail: wide enough that the settings themselves keep the room
    /// they had, but never past <paramref name="maximum"/> — the cockpit's own cap, because a dialog that opens
    /// wider than the window behind it opens with its content cut off. On a cockpit too narrow to afford both,
    /// the cap wins and the rail does come out of the settings' width; the dialog is resizable from there.
    /// </summary>
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

    // The view gets the same inset as the footer already had. Without it a plugin's settings sat flush against
    // the window edge — every plugin would otherwise have to remember its own margin, and they did not, so the
    // padding belongs here where the host owns the chrome. The inset is a Border *inside* the scrolled content,
    // not Padding on the ScrollViewer: Avalonia leaves a ScrollViewer's own padding out of the scroll extent, so
    // a tall view could not scroll its last ~24px clear and it stayed under the footer.
    private static ScrollViewer _ScrolledView(Control view) =>
        new() { Content = new Border { Padding = new Thickness(14, 12), Child = view } };

    // The same rail the Options dialog grew in AC-69, over the same Theme.axaml styles — a plugin's settings and
    // the cockpit's own read as one thing, and there is no second copy of the visual language to keep in step.
    // The view itself stays the scrolled content and only swaps what it shows, so it is attached for the whole
    // dialog: a settings view that loads its profiles when it attaches, or drops a subscription when it detaches,
    // behaves exactly as it does without a rail.
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
