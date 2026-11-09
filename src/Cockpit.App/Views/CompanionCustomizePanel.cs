using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Material.Icons;

namespace Cockpit.App.Views;

// AC-2190: the companion-strook mockup's "Customize strip" pop-out — a back arrow to the options, the strip's items as
// rows you drag by their handle (or move with the arrows that show on hover and focus), what can be added, what went
// missing, and Reset at the foot.
internal sealed class CompanionCustomizePanel : StackPanel
{
    private static readonly DataFormat<string> ItemFormat = DataFormat.CreateInProcessFormat<string>("cockpit/companion-strip-item");
    private readonly CompanionCustomizationModel _model;
    private readonly Func<string, Task> _enable;
    private readonly Func<string, string> _source;
    private readonly StackPanel _rows = new() { Margin = new Thickness(8, 0, 8, 8) };
    private string? _dragging;

    public CompanionCustomizePanel(CompanionCustomizationModel model, Func<string, Task> enable, Action close, Action? back = null, Func<string, string>? source = null)
    {
        _model = model;
        _enable = enable;
        _source = source ?? (_ => "Cockpit");
        Styles.Add(_RowStyles());
        var actions = back is null ? [] : new Control[] { CompanionPanelChrome.IconButton(MaterialIconKind.ArrowLeft, "Back", back) };
        var title = CompanionPanelChrome.Text("Customize strip", weight: FontWeight.SemiBold);
        var head = new DockPanel { Margin = new Thickness(back is null ? 14 : 10, 10, 10, 8) };
        var closeButton = CompanionPanelChrome.IconButton(MaterialIconKind.Close, "Close", close);
        DockPanel.SetDock(closeButton, Dock.Right);
        head.Children.Add(closeButton);
        foreach (var action in actions)
        {
            DockPanel.SetDock(action, Dock.Left);
            action.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(action);
        }
        title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title);
        Children.Add(head);
        Children.Add(_rows);
        var reset = new Button { Content = "Reset", Classes = { "Ghost", "Compact" } };
        ToolTip.SetTip(reset, "Back to the default strip");
        reset.Click += (_, _) => _model.Reset();
        var hint = CompanionPanelChrome.Text("New plugin items wait under Available.", "CockpitTextSecondaryBrush", 11);
        DockPanel.SetDock(reset, Dock.Right);
        var foot = CompanionPanelChrome.Footer(reset, hint);
        ((DockPanel)foot.Child!).LastChildFill = true;
        Children.Add(foot);
        AddHandler(DragDrop.DragOverEvent, _OnDragOver);
        AddHandler(DragDrop.DropEvent, _OnDrop);
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
            var text = CompanionPanelChrome.Text(notice, "CockpitTextSecondaryBrush");
            text.Margin = new Thickness(6, 0, 6, 6);
            _rows.Children.Add(text);
        }
        var onStrip = _model.OnStrip.ToList();
        _rows.Children.Add(_Label("On the strip"));
        foreach (var id in onStrip)
        {
            var pinned = id == CompanionCustomizationModel.AssistantId;
            var row = _Row(id, pinned ? "Always first, always there" : _source(id), handle: !pinned, out var actions);
            if (pinned)
            {
                var lockIcon = CockpitIcons.Icon(MaterialIconKind.LockOutline, 14);
                lockIcon.Bind(Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, lockIcon.GetResourceObservable("CockpitTextFaintBrush"));
                actions.Children.Add(lockIcon);
                actions.Classes.Add("pinned");
            }
            else
            {
                var up = _Action("Move up", MaterialIconKind.ChevronUp, () => _model.Move(id, -1));
                up.IsEnabled = onStrip.IndexOf(id) > 1;
                var down = _Action("Move down", MaterialIconKind.ChevronDown, () => _model.Move(id, 1));
                down.IsEnabled = onStrip.IndexOf(id) < onStrip.Count - 1;
                var remove = _Action("Take off the strip", MaterialIconKind.MinusCircleOutline, () => _model.SetEnabled(id, false));
                _AddAction(actions, up, id);
                _AddAction(actions, down, id);
                _AddAction(actions, remove, id);
                ToolTip.SetTip(row, "Drag, or Up/Down, to reorder · Enter to take off");
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
        _Group("Unavailable", _model.Unavailable, id => $"{_source(id)} is disabled. Place kept.", (id, actions) =>
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
        _Group("Available", _model.Available, _source, (id, actions) =>
        {
            var add = _Action("Add", MaterialIconKind.Plus, () => _model.SetEnabled(id, true), label: true);
            _AddAction(actions, add, id);
        });
        if (focused is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var controls = this.GetVisualDescendants().OfType<Control>().ToList();
                var target = controls.FirstOrDefault(control => control.Tag as string == focused && control.IsEnabled);
                var separator = focused.LastIndexOf(':');
                target ??= separator > 0 ? controls.FirstOrDefault(control => control.Tag as string == focused[..separator]) : null;
                target?.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    private void _Group(string title, IEnumerable<string> ids, Func<string, string> subtitle, Action<string, StackPanel> addActions)
    {
        var entries = ids.ToList();
        if (entries.Count == 0)
        {
            return;
        }

        _rows.Children.Add(_Label(title));
        foreach (var id in entries)
        {
            var row = _Row(id, subtitle(id), handle: false, out var actions);
            row.Classes.Set("gone", title == "Unavailable");
            actions.Classes.Add("pinned");
            addActions(id, actions);
            row.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && ReferenceEquals(e.Source, row) && actions.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled) is { } action)
                {
                    action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                }
            };
            _rows.Children.Add(row);
        }
    }

    // The mockup's .mrow: handle, icon, title over its source, and the row's actions at the end.
    private Border _Row(string id, string subtitle, bool handle, out StackPanel actions)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("15,Auto,*,Auto"), ColumnSpacing = 8 };
        var row = new Border { Padding = new Thickness(6, 5), CornerRadius = new CornerRadius(7), Focusable = true, Tag = id, Classes = { "companionRow" }, Child = grid };
        AutomationProperties.SetName(row, _model.Title(id));
        if (handle)
        {
            var grip = new Border { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeNorthSouth), Child = CockpitIcons.Icon(MaterialIconKind.DragVertical, 15) };
            grip.Classes.Add("companionRowHandle");
            ToolTip.SetTip(grip, "Drag to reorder");
            grip.PointerPressed += async (_, e) => await _DragAsync(e, row, id);
            grid.Children.Add(grip);
        }
        var icon = CockpitIcons.Icon(_model.Tools.FirstOrDefault(tool => tool.Id == id)?.IconKind ?? MaterialIconKind.PuzzleOutline, 16);
        icon.Classes.Add("companionRowIcon");
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = CompanionPanelChrome.Text(_model.Title(id), weight: FontWeight.Medium);
        name.Classes.Add("companionRowTitle");
        text.Children.Add(name);
        text.Children.Add(CompanionPanelChrome.Text(subtitle, "CockpitTextSecondaryBrush", 11));
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Classes = { "companionRowActions" } };
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);
        return row;
    }

    // Plain in-window drag and drop: the dragged row's id rides along, the row under the pointer is where it lands.
    private async Task _DragAsync(PointerPressedEventArgs e, Border row, string id)
    {
        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        _dragging = id;
        row.Classes.Add("dragging");
        try
        {
            using var data = new DataTransfer();
            data.Add(DataTransferItem.Create(ItemFormat, id));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            row.Classes.Remove("dragging");
            _dragging = null;
        }
    }

    private string? _TargetOf(DragEventArgs e) =>
        _dragging is not null && e.DataTransfer.Contains(ItemFormat)
        && (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("companionRow")) is { Tag: string target }
        && target != CompanionCustomizationModel.AssistantId && _model.OnStrip.Contains(target) ? target : null;

    private void _OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _TargetOf(e) is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void _OnDrop(object? sender, DragEventArgs e)
    {
        if (_dragging is { } id && _TargetOf(e) is { } target)
        {
            _model.MoveTo(id, target);
        }
        e.Handled = true;
    }

    private static TextBlock _Label(string text)
    {
        var label = CompanionPanelChrome.Label(text);
        label.Margin = new Thickness(6, 4, 6, 4);
        return label;
    }

    private static void _AddAction(StackPanel actions, Button button, string id)
    {
        button.Tag = id + ":" + AutomationProperties.GetName(button);
        actions.Children.Add(button);
    }

    private static Button _Action(string name, MaterialIconKind? icon, Action? action = null, bool label = false)
    {
        object content = icon is { } kind
            ? label
                ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { CockpitIcons.Icon(kind, 14), new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center } } }
                : CockpitIcons.Icon(kind, 15)
            : name;
        var button = new Button { Content = content, Classes = { "Ghost", "Compact" } };
        if (icon is not null && !label)
        {
            button.Padding = new Thickness(4);
        }
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        if (action is not null)
        {
            button.Click += (_, _) => action();
        }
        return button;
    }

    private static Styles _RowStyles() =>
    [
        new Style(x => x.OfType<Border>().Class("companionRow"))
        {
            Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent) },
        },
        new Style(x => x.OfType<Border>().Class("companionRow").Class(":pointerover"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CockpitGhostHoverBrush")) },
        },
        new Style(x => x.OfType<Border>().Class("companionRow").Class("dragging"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CockpitAccentSelectionBrush")) },
        },
        new Style(x => x.OfType<Material.Icons.Avalonia.MaterialIcon>().Class("companionRowIcon"))
        {
            Setters = { new Setter(Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, new DynamicResourceExtension("CockpitTextSecondaryBrush")) },
        },
        new Style(x => x.OfType<Border>().Class("companionRowHandle"))
        {
            Setters = { new Setter(Border.VerticalAlignmentProperty, VerticalAlignment.Center) },
        },
        new Style(x => x.OfType<StackPanel>().Class("companionRowActions"))
        {
            Setters = { new Setter(OpacityProperty, 0d) },
        },
        new Style(x => x.OfType<Border>().Class("companionRow").Class(":pointerover").Descendant().OfType<StackPanel>().Class("companionRowActions"))
        {
            Setters = { new Setter(OpacityProperty, 1d) },
        },
        new Style(x => x.OfType<Border>().Class("companionRow").Class(":focus-within").Descendant().OfType<StackPanel>().Class("companionRowActions"))
        {
            Setters = { new Setter(OpacityProperty, 1d) },
        },
        new Style(x => x.OfType<StackPanel>().Class("companionRowActions").Class("pinned"))
        {
            Setters = { new Setter(OpacityProperty, 1d) },
        },
        new Style(x => x.OfType<Border>().Class("gone").Descendant().OfType<TextBlock>().Class("companionRowTitle"))
        {
            Setters = { new Setter(OpacityProperty, 0.55) },
        },
    ];
}
