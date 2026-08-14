using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// Renders the registered toolbar actions (`ICockpitHost.AddToolbarAction`, AC-91) as compact buttons on the
// workspace tab strip (AC-772), where they are reachable from every workspace type rather than only from a
// Sessions workspace that already has a session in it. Up to `InlineLimit` show inline; beyond that they collapse
// into a single overflow (⋯) button with a flyout, so the narrow strip never overflows. Contributes nothing and
// takes no space when nothing registers an action — which is what a fresh install without plugins looks like.
// Reads its `CockpitViewModel` from the inherited `StyledElement.DataContext`, so it renders wherever that view
// model is in scope (incl. headless).
internal sealed class PluginToolbarHost : StackPanel
{
    private const int InlineLimit = 3;

    // Past this the label trims. Wide enough for "Depot servers", narrow enough that three of them still leave the
    // tab strip its own room on a small window.
    private const double LabelMaxWidth = 110;

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
                Children.Add(_IconButton(action));
            }
        }
        else
        {
            Children.Add(_OverflowButton(actions));
        }
    }

    private Button _IconButton(PluginToolbarAction entry)
    {
        var action = entry.Action;

        // Icon plus label, not a bare icon (AC-772): an icon alone on a strip that is now always on screen is a
        // guess for anyone who did not install the plugin themselves. The label trims rather than pushes the tab
        // strip aside, so a long title costs the toolbar width it has and no more.
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new MaterialIcon
        {
            Kind = action.Icon ?? MaterialIconKind.PuzzleOutline,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = action.Title,
            MaxWidth = LabelMaxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Default button chrome (like the workspace gear) so it reads as a button, not a bare icon.
        var button = new Button
        {
            Padding = new Thickness(8, 4),
            Content = content,
        };
        // The tooltip carries the untrimmed title; the automation name is what a screen reader announces, which a
        // tooltip on its own never reaches.
        ToolTip.SetTip(button, action.Title);
        AutomationProperties.SetName(button, action.Title);
        button.Click += async (_, _) => await _Invoke(entry);
        return button;
    }

    private Button _OverflowButton(IReadOnlyList<PluginToolbarAction> actions)
    {
        var button = new Button
        {
            Padding = new Thickness(8, 4),
            Content = new MaterialIcon { Kind = MaterialIconKind.DotsHorizontal, Width = 14, Height = 14 },
        };
        ToolTip.SetTip(button, "More actions");
        AutomationProperties.SetName(button, "More actions");

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var panel = new StackPanel { Spacing = 2, MinWidth = 200, Margin = new Thickness(4) };
        foreach (var action in actions)
        {
            panel.Children.Add(_OverflowRow(action, flyout));
        }

        flyout.Content = panel;
        button.Flyout = flyout;
        return button;
    }

    private Button _OverflowRow(PluginToolbarAction entry, Flyout flyout)
    {
        var action = entry.Action;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new MaterialIcon { Kind = action.Icon ?? MaterialIconKind.PuzzleOutline, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = action.Title, VerticalAlignment = VerticalAlignment.Center });

        var row = new Button
        {
            Classes = { "Subtle" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = content,
        };
        AutomationProperties.SetName(row, action.Title);
        row.Click += async (_, _) =>
        {
            flyout.Hide();
            await _Invoke(entry);
        };
        return row;
    }

    private async Task _Invoke(PluginToolbarAction entry)
    {
        try
        {
            await entry.Action.OnInvoke();
        }
        catch (Exception exception)
        {
            // Fail-soft, but not silent (AC-772 criterion 6): the cockpit stays up, and the failure lands in
            // PluginDiagnostics next to every other contribution failure, so the startup banner and the plugin
            // manager can say which plugin broke instead of the operator seeing a button that does nothing.
            _cockpit?.ReportToolbarActionFailure(entry.PluginId, entry.Action.Title, exception.Message);
        }
    }

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
