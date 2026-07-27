using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// The dashboard pane of saved prompts (AC-53): the same templates the library holds, one click to drop one into
/// the active session. The palette behind Ctrl+Shift+P already does this in one keystroke; what a pane adds is that
/// the prompts you reach for are simply <em>there</em>, without a gesture to remember.
/// </summary>
/// <remarks>
/// It shows every saved template rather than a curated set. The ticket asked for favourites, and a favourites flag
/// would mean a field on <see cref="PromptTemplate"/>, a storage migration and a toggle in the library dialog — real
/// work for a curation problem nobody has yet, since the list is small enough to read. When it is not, that is the
/// moment to add the flag, and the pane is where it will show first.
/// <para>
/// Built in <c>Initialize</c> where <see cref="ICockpitHost"/> is in scope, so the closure carries the host's actions
/// (which is what injects) alongside this pane's own <see cref="IWidgetContext"/> — the same shape the GitHub pull
/// requests widget uses.
/// </para>
/// </remarks>
internal sealed class PromptQuickLauncherWidget : UserControl
{
    private readonly PromptLibrarySettings _settings;
    private readonly ICockpitActions _actions;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _empty;

    public PromptQuickLauncherWidget(PromptLibrarySettings settings, ICockpitActions actions, IWidgetContext context)
    {
        _settings = settings;
        _actions = actions;

        _empty = new TextBlock
        {
            Text = "No prompts saved yet — add one in the Prompt Library.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
            Margin = new Thickness(2, 4, 2, 0),
        };

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { _rows, _empty } },
        };

        // The pane's own ↻ and anything that changes the stored list both land here; reloading from storage is
        // cheap (a JSON read) and is the only way this learns about an edit made in the library dialog.
        context.RefreshRequested += (_, _) => _Reload();
        _Reload();
    }

    private void _Reload()
    {
        var templates = _settings.Load();
        _rows.Children.Clear();
        foreach (var template in templates)
        {
            _rows.Children.Add(_Row(template));
        }

        _empty.IsVisible = templates.Count == 0;
    }

    private Control _Row(PromptTemplate template)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = template.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6),
            Background = Brushes.Transparent,
            // The body is what actually gets sent, so it is what the tooltip shows — the name is the operator's
            // label for it and can say anything.
            [ToolTip.TipProperty] = template.Body,
        };

        button.Click += async (_, _) => await PromptInjection.SendAsync(_actions, template);
        return button;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) is true ? value as IBrush : null;
}
