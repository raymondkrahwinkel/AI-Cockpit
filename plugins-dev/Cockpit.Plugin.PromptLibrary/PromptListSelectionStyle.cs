using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// Gives a prompt <see cref="ListBox"/> a clearly visible selected-item highlight (the default Fluent
/// selection was too faint to see which template/prompt is active). It styles the selected item's content
/// presenter directly with the app's accent colour — the reliable way to recolour a Fluent
/// <see cref="ListBoxItem"/>, since the theme resource-key override proved too faint.
/// </summary>
internal static class PromptListSelectionStyle
{
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#E2795A"));
    private static readonly IBrush Hover = new SolidColorBrush(Color.Parse("#2A2E37"));

    public static void Apply(ListBox list)
    {
        list.Styles.Add(_ItemBackground(selected: true, hover: true, Selected));
        list.Styles.Add(_ItemBackground(selected: true, hover: false, Selected));
        list.Styles.Add(_ItemBackground(selected: false, hover: true, Hover));
    }

    // A style setting the background of a ListBoxItem's PART_ContentPresenter in the given state.
    private static Style _ItemBackground(bool selected, bool hover, IBrush brush)
    {
        var style = new Style(x =>
        {
            var item = x.OfType<ListBoxItem>();
            if (selected)
            {
                item = item.Class(":selected");
            }

            if (hover)
            {
                item = item.Class(":pointerover");
            }

            return item.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
        });
        style.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, brush));
        return style;
    }
}
