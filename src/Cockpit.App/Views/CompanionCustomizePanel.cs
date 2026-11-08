using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Material.Icons;

namespace Cockpit.App.Views;

internal sealed class CompanionCustomizePanel : StackPanel
{
    private readonly CompanionCustomizationModel _model;
    private readonly Func<string, Task> _enable;
    private readonly StackPanel _rows = new() { Spacing = 4 };

    public CompanionCustomizePanel(CompanionCustomizationModel model, Func<string, Task> enable, Action close)
    {
        _model = model;
        _enable = enable;
        Margin = new Thickness(16);
        Spacing = 12;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(_Text("Customize strip", "CockpitTextPrimaryBrush", bold: true));
        var dismiss = _Action("Close", MaterialIconKind.Close, close);
        Grid.SetColumn(dismiss, 1);
        header.Children.Add(dismiss);
        Children.Add(header);
        Children.Add(_rows);
        Children.Add(_Text("New plugin items wait under Available.", "CockpitTextFaintBrush"));
        AttachedToVisualTree += (_, _) =>
        {
            _model.Changed += _Refresh;
            _model.Refreshed += _Refresh;
            _Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _model.Changed -= _Refresh;
            _model.Refreshed -= _Refresh;
        };
        _Refresh();
    }

    private void _Refresh()
    {
        var focused = (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control)?.Tag as string;
        _rows.Children.Clear();
        if (_model.Notice is { } notice)
        {
            _rows.Children.Add(_Text(notice, "CockpitTextSecondaryBrush"));
        }
        var onStrip = _model.OnStrip.ToList();
        _rows.Children.Add(_Text("On strip", "CockpitTextFaintBrush"));
        foreach (var id in onStrip)
        {
            var pinned = id == CompanionCustomizationModel.AssistantId;
            var row = _Row(id, pinned ? "Always first, always there" : "On the strip", out var actions);
            if (pinned)
            {
                actions.Children.Add(CockpitIcons.Icon(MaterialIconKind.LockOutline, 14));
            }
            else
            {
                var up = _Action("Move up", MaterialIconKind.ArrowUp, () => _model.Move(id, -1));
                up.IsEnabled = onStrip.IndexOf(id) > 1;
                var down = _Action("Move down", MaterialIconKind.ArrowDown, () => _model.Move(id, 1));
                down.IsEnabled = onStrip.IndexOf(id) < onStrip.Count - 1;
                var remove = _Action("Remove from strip", MaterialIconKind.Minus, () => _model.SetEnabled(id, false));
                _AddAction(actions, up, id);
                _AddAction(actions, down, id);
                _AddAction(actions, remove, id);
                ToolTip.SetTip(row, "Up/Down to reorder · Enter to remove");
                row.KeyDown += (_, e) =>
                {
                    if (e.Key is Key.Up or Key.Down)
                    {
                        _model.Move(id, e.Key == Key.Up ? -1 : 1);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Enter && ReferenceEquals(e.Source, row))
                    {
                        _model.SetEnabled(id, false);
                        e.Handled = true;
                    }
                };
            }
            _rows.Children.Add(row);
        }
        _Group("Available", _model.Available, (id, actions) =>
        {
            var add = _Action("Add", null, () => _model.SetEnabled(id, true));
            _AddAction(actions, add, id);
        });
        _Group("Unavailable", _model.Unavailable, (id, actions) =>
        {
            var enable = _Action("Enable", null);
            enable.IsEnabled = _model.PluginId(id) is not null;
            ToolTip.SetShowOnDisabled(enable, true);
            ToolTip.SetTip(enable, enable.IsEnabled ? "Review and enable this plugin" : "This tool's plugin is not known. Reinstall or enable it in Plugins.");
            enable.Click += async (_, _) =>
            {
                enable.IsEnabled = false;
                try
                {
                    await _enable(id);
                }
                finally
                {
                    _Refresh();
                }
            };
            _AddAction(actions, enable, id);
            _AddAction(actions, _Action("Forget this item", MaterialIconKind.Close, () => _model.Forget(id)), id);
        });
        if (focused is not null)
        {
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Tag as string == focused && control.IsEnabled)?.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void _Group(string title, IEnumerable<string> ids, Action<string, StackPanel> addActions)
    {
        var entries = ids.ToList();
        _rows.Children.Add(_Text(title, "CockpitTextFaintBrush"));
        if (entries.Count == 0)
        {
            _rows.Children.Add(_Text(title == "Available" ? "All available tools are on the strip." : "No unavailable tools.", "CockpitTextSecondaryBrush"));
        }
        foreach (var id in entries)
        {
            var row = _Row(id, title == "Unavailable" ? "Plugin unavailable. Place kept." : "Ready to add", out var actions);
            addActions(id, actions);
            _rows.Children.Add(row);
        }
    }

    private Control _Row(string id, string subtitle, out StackPanel actions)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 4), Focusable = true, Tag = id };
        AutomationProperties.SetName(row, _model.Title(id));
        var icon = CockpitIcons.Icon(_model.Tools.FirstOrDefault(tool => tool.Id == id)?.IconKind ?? MaterialIconKind.PuzzleOutline, 16);
        row.Children.Add(icon);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_Text(_model.Title(id), "CockpitTextPrimaryBrush", bold: true));
        text.Children.Add(_Text(subtitle, "CockpitTextSecondaryBrush"));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        return row;
    }

    private static void _AddAction(StackPanel actions, Button button, string id)
    {
        button.Tag = id + ":" + AutomationProperties.GetName(button);
        actions.Children.Add(button);
    }

    private static TextBlock _Text(string value, string brush, bool bold = false)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(brush));
        return text;
    }

    private static Button _Action(string label, MaterialIconKind? icon, Action? action = null)
    {
        var button = new Button { Content = icon is { } kind ? CockpitIcons.Icon(kind, 14) : label, Classes = { "Ghost", "Compact" } };
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        if (action is not null)
        {
            button.Click += (_, _) => action();
        }
        return button;
    }
}
