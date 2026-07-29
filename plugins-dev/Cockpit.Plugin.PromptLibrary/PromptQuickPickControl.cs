using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// A compact "insert a prompt fast" palette (#: prompt quick-inject): a search box over the saved templates
/// and a list — type to filter, ↑/↓ to move, Enter or a click to inject the prompt into the active session and
/// close. Opened from the "Insert prompt" left-menu button or its keyboard shortcut. Unlike the full Prompt
/// Library dialog it does not prompt for <c>{{variable}}</c> values — it drops the template body straight in,
/// leaving any placeholders for you to fill in the input — so it stays a one-keystroke action.
/// </summary>
internal sealed class PromptQuickPickControl : UserControl
{
    private readonly PromptLibrarySettings _settings;
    private readonly ICockpitActions _actions;
    private readonly Border _searchBar;
    private readonly TextBlock _icon;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ObservableCollection<PromptTemplate> _visible = [];
    private List<PromptTemplate> _all = [];

    public PromptQuickPickControl(PromptLibrarySettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        // A spotlight-style search bar: a rounded pill with an accent spark icon and a borderless input, so the
        // whole thing reads as one search field rather than a boxed TextBox.
        _search = new TextBox
        {
            PlaceholderText = "Search your prompts…",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontSize = 15,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _search.TextChanged += (_, _) => _ApplyFilter();
        // Tunnel on the control root (an ancestor of the TextBox), so the arrow keys are caught before the
        // TextBox's own handling consumes Up/Down — a handler on the TextBox itself loses to its class handler.
        AddHandler(KeyDownEvent, _OnSearchKeyDown, RoutingStrategies.Tunnel);

        _icon = new TextBlock
        {
            Text = "›",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 12, 0),
            Foreground = _Brush("CockpitAccentBrush", "#2563eb"),
        };
        var searchInner = new DockPanel();
        DockPanel.SetDock(_icon, Dock.Left);
        searchInner.Children.Add(_icon);
        searchInner.Children.Add(_search);

        _searchBar = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush", "#0c0e12"),
            BorderBrush = _Brush("CockpitHairlineBrush", "#2a2f39"),
            BorderThickness = new Thickness(1),
            CornerRadius = _Radius("CockpitControlRadius", 9),
            Padding = new Thickness(14, 11),
            Child = searchInner,
        };

        _list = new ListBox
        {
            ItemsSource = _visible,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<PromptTemplate>((template, _) =>
                new TextBlock { Text = template?.Name, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 5) }, true),
        };
        // A click injects the item just selected by that same click.
        _list.AddHandler(PointerReleasedEvent, (_, _) => _ = _InjectSelectedAndCloseAsync(), RoutingStrategies.Tunnel);

        _status = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(4, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(12) };
        Grid.SetRow(_searchBar, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_status, 2);
        _list.Margin = new Thickness(0, 10, 0, 0);
        layout.Children.Add(_searchBar);
        layout.Children.Add(_list);
        layout.Children.Add(_status);
        Content = layout;

        _all = [.. _settings.Load()];
        _ApplyFilter();

        AttachedToVisualTree += (_, _) =>
            // Focus after the dialog window has settled — focusing during attach doesn't stick when the palette
            // is opened from the keyboard shortcut, so the search box wouldn't be ready to type into.
            Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The host's theme brush, resolved at call time. The fallback hex is only reached with no
    /// <see cref="Application"/> (designer, headless test) and is held equal to its token by the repository's theme
    /// guard. This replaced three hand-held fallbacks that were applied first and swapped for the real brushes on
    /// attach — a two-step that left the palette's own accent frozen at the pre-AC-334 orange in the one place it
    /// could still be seen.
    /// </summary>
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));

    /// <summary>The host's geometry token, so a plugin's box rounds like the app's inputs do.</summary>
    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);

    private void _ApplyFilter()
    {
        var query = _search.Text?.Trim();
        _visible.Clear();
        foreach (var template in _all)
        {
            if (string.IsNullOrEmpty(query)
                || template.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || template.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _visible.Add(template);
            }
        }

        if (_visible.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        _status.Text = _all.Count == 0
            ? "No saved prompts yet — add some in the Prompt Library."
            : $"{_visible.Count} of {_all.Count} prompt(s).";
    }

    private void _OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _ = _InjectSelectedAndCloseAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                _Close();
                e.Handled = true;
                break;
            case Key.Down:
                _Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                _Move(-1);
                e.Handled = true;
                break;
        }
    }

    private void _Move(int delta)
    {
        if (_visible.Count == 0)
        {
            return;
        }

        var next = _list.SelectedIndex + delta;
        _list.SelectedIndex = Math.Clamp(next, 0, _visible.Count - 1);
    }

    private async Task _InjectSelectedAndCloseAsync()
    {
        if (_list.SelectedItem is not PromptTemplate template)
        {
            return;
        }

        if (_actions.HasActiveSession)
        {
            await _actions.InjectIntoActiveSessionAsync(template.Body);
        }
        else
        {
            await _actions.SetClipboardTextAsync(template.Body);
        }

        _Close();
    }

    private void _Close() => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
