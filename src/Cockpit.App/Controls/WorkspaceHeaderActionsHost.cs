using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Controls;

// AC-1711: the active plugin workspace's own header buttons (`WorkspaceTypeRegistration.HeaderActions`) on the tab
// strip, next to the global toolbar actions, so "Dock in the rail" sits in the host's row as the browser mockup
// draws it. Present only while a workspace of that type is active; nothing at all otherwise.
internal sealed class WorkspaceHeaderActionsHost : StackPanel
{
    private WorkspacesViewModel? _workspaces;
    private CockpitViewModel? _cockpit;

    public WorkspaceHeaderActionsHost()
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
        _workspaces = _cockpit?.Workspaces;
        if (_workspaces is null)
        {
            return;
        }

        _workspaces.PropertyChanged += _OnWorkspacesChanged;
        _Render();
    }

    private void _OnWorkspacesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspacesViewModel.ActiveHeaderActions))
        {
            _Render();
        }
    }

    private void _Render()
    {
        Children.Clear();
        foreach (var action in _workspaces?.ActiveHeaderActions ?? [])
        {
            Children.Add(_Button(action));
        }
    }

    private Button _Button(ToolbarAction action)
    {
        var button = new Button
        {
            Padding = new Thickness(8, 4),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new MaterialIcon { Kind = action.Icon ?? MaterialIconKind.PuzzleOutline, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = action.Title, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(button, action.Title);
        AutomationProperties.SetName(button, action.Title);
        button.Click += async (_, _) =>
        {
            try
            {
                await action.OnInvoke();
            }
            catch (Exception exception)
            {
                // Fail-soft and reported like a toolbar action; a type id is namespaced by its plugin ("browser.tab").
                var typeId = _workspaces?.Active?.Type.Id ?? string.Empty;
                _cockpit?.ReportToolbarActionFailure(typeId.Split('.')[0], action.Title, exception.Message);
            }
        };
        return button;
    }

    private void _Detach()
    {
        if (_workspaces is not null)
        {
            _workspaces.PropertyChanged -= _OnWorkspacesChanged;
        }

        Children.Clear();
        _workspaces = null;
        _cockpit = null;
    }
}
