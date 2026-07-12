using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ObservableCollection<PromptTemplate> _visible = [];
    private List<PromptTemplate> _all = [];

    public PromptQuickPickControl(PromptLibrarySettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        _search = new TextBox { PlaceholderText = "Search prompts — Enter to insert, Esc to close" };
        _search.TextChanged += (_, _) => _ApplyFilter();
        _search.AddHandler(KeyDownEvent, _OnSearchKeyDown, RoutingStrategies.Tunnel);

        _list = new ListBox
        {
            ItemsSource = _visible,
            ItemTemplate = new FuncDataTemplate<PromptTemplate>((template, _) =>
                new TextBlock { Text = template?.Name, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 3) }, true),
        };
        PromptListSelectionStyle.Apply(_list);
        // A click injects the item just selected by that same click.
        _list.AddHandler(PointerReleasedEvent, (_, _) => _ = _InjectSelectedAndCloseAsync(), RoutingStrategies.Tunnel);

        _status = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(12) };
        Grid.SetRow(_search, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_status, 2);
        _list.Margin = new Thickness(0, 8, 0, 0);
        layout.Children.Add(_search);
        layout.Children.Add(_list);
        layout.Children.Add(_status);
        Content = layout;

        _all = [.. _settings.Load()];
        _ApplyFilter();

        AttachedToVisualTree += (_, _) => _search.Focus();
    }

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
