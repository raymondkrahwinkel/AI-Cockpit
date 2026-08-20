using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Cockpit.Plugin.Diagram.Collab;

// AC-911: the "what you get" grid shared by the quick-start dialog and by the diagram surface's own "Insert
// template…" button — one radio-selected strip of thumbnails, Blank first and preselected (criterion 7). What
// differs per surface is only the template list and how to draw one template's thumbnail.
internal static class SurfaceTemplateStrip
{
    public static (Control Strip, Func<SurfaceTemplate> Selected) Build(
        IReadOnlyList<SurfaceTemplate> templates,
        Func<SurfaceTemplate, Control> preview)
    {
        var selected = templates[0];
        var wrap = new WrapPanel();

        foreach (var template in templates)
        {
            var entry = new RadioButton
            {
                GroupName = "SurfaceTemplate",
                IsChecked = template == templates[0],
                Margin = new Thickness(2),
                Padding = new Thickness(6),
                Content = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new Viewbox { Width = 150, Height = 100, Child = preview(template) },
                        new TextBlock { Text = template.Name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            entry.Click += (_, _) => selected = template;
            wrap.Children.Add(entry);
        }

        return (wrap, () => selected);
    }
}
