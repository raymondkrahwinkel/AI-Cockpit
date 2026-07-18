using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Controls;

/// <summary>
/// Renders the plugin-registered Sessions-toolbar actions (<c>ICockpitHost.AddToolbarAction</c>, AC-91) as compact
/// icon buttons next to the workspace gear. Up to <see cref="InlineLimit"/> show inline; beyond that they collapse
/// into a single overflow (⋯) button with a flyout, so the narrow strip never overflows. Contributes nothing and
/// takes no space when no plugin registers an action. Reads its <see cref="CockpitViewModel"/> from the inherited
/// <see cref="StyledElement.DataContext"/>, so it renders wherever that view model is in scope (incl. headless).
/// </summary>
internal sealed class PluginToolbarHost : StackPanel
{
    private const int InlineLimit = 3;
    private CockpitViewModel? _cockpit;

    public PluginToolbarHost()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Spacing = 4;

        AttachedToVisualTree += (_, _) => _Rebind();
        DetachedFromVisualTree += (_, _) => _Detach();
        DataContextChanged += (_, _) => _Rebind();
    }

    private void _Rebind()
    {
        _Detach();

        _cockpit = DataContext as CockpitViewModel;
        if (_cockpit is null)
        {
            return;
        }

        _cockpit.PluginToolbarActions.CollectionChanged += _OnActionsChanged;
        // The operator can reorder/hide plugins in the manager (#72) — VisibleToolbarActions reflects that, so rebuild on it too.
        _cockpit.PluginMenuChanged += _OnMenuChanged;
        _Render();
    }

    private void _OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _Render();

    private void _OnMenuChanged(object? sender, EventArgs e) => _Render();

    private void _Render()
    {
        Children.Clear();
        if (_cockpit is null)
        {
            return;
        }

        var actions = _cockpit.VisibleToolbarActions;
        if (actions.Count == 0)
        {
            return;
        }

        if (actions.Count <= InlineLimit)
        {
            foreach (var action in actions)
            {
                Children.Add(_IconButton(action.Action));
            }
        }
        else
        {
            Children.Add(_OverflowButton(actions));
        }
    }

    private static Button _IconButton(ToolbarAction action)
    {
        // Default button chrome (like the workspace gear next to it) so it reads as a button, not a bare icon.
        var button = new Button
        {
            Padding = new Thickness(8, 4),
            Content = new MaterialIcon { Kind = _Kind(action.Icon), Width = 14, Height = 14 },
        };
        ToolTip.SetTip(button, action.Title);
        button.Click += async (_, _) => await _Invoke(action);
        return button;
    }

    private static Button _OverflowButton(IReadOnlyList<PluginToolbarAction> actions)
    {
        var button = new Button
        {
            Padding = new Thickness(8, 4),
            Content = new MaterialIcon { Kind = MaterialIconKind.DotsHorizontal, Width = 14, Height = 14 },
        };
        ToolTip.SetTip(button, "Plugin actions");

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var panel = new StackPanel { Spacing = 2, MinWidth = 200, Margin = new Thickness(4) };
        foreach (var action in actions)
        {
            panel.Children.Add(_OverflowRow(action.Action, flyout));
        }

        flyout.Content = panel;
        button.Flyout = flyout;
        return button;
    }

    private static Button _OverflowRow(ToolbarAction action, Flyout flyout)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new MaterialIcon { Kind = _Kind(action.Icon), Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = action.Title, VerticalAlignment = VerticalAlignment.Center });

        var row = new Button
        {
            Classes = { "Subtle" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = content,
        };
        row.Click += async (_, _) =>
        {
            flyout.Hide();
            await _Invoke(action);
        };
        return row;
    }

    private static async Task _Invoke(ToolbarAction action)
    {
        try
        {
            await action.OnInvoke();
        }
        catch (Exception)
        {
            // Fail-soft: a plugin's toolbar action must not crash the cockpit UI.
        }
    }

    private static MaterialIconKind _Kind(string? icon) =>
        icon is not null && Enum.TryParse<MaterialIconKind>(icon, ignoreCase: true, out var kind)
            ? kind
            : MaterialIconKind.PuzzleOutline;

    private void _Detach()
    {
        if (_cockpit is not null)
        {
            _cockpit.PluginToolbarActions.CollectionChanged -= _OnActionsChanged;
            _cockpit.PluginMenuChanged -= _OnMenuChanged;
        }

        Children.Clear();
        _cockpit = null;
    }
}
