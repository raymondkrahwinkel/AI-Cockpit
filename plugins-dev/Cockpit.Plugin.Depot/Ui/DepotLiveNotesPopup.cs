using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.UI;

// What the "N live notes · Depot" status line opens: the live notes of every connection, one click to open and join one.
internal sealed class DepotLiveNotesPopup : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly DepotNotesModel _model;
    private readonly Func<IReadOnlyList<DepotConnectionRegistration>> _connections;
    private readonly Action _close;
    private readonly StackPanel _rows = new() { Spacing = 2 };

    public DepotLiveNotesPopup(ICockpitUiHost host, DepotNotesModel model,
        Func<IReadOnlyList<DepotConnectionRegistration>> connections, Action close)
    {
        (_host, _model, _connections, _close) = (host, model, connections, close);
        var closeButton = new Button { Classes = { "Subtle" }, Padding = new(6, 0), Content = new MaterialIcon { Kind = MaterialIconKind.Close, Width = 12, Height = 12 } };
        ToolTip.SetTip(closeButton, "Close");
        Avalonia.Automation.AutomationProperties.SetName(closeButton, "Close");
        closeButton.Click += (_, _) => close();
        var header = new DockPanel();
        DockPanel.SetDock(closeButton, Dock.Right);
        header.Children.Add(closeButton);
        header.Children.Add(new TextBlock { Text = "Live notes", FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        var footer = _Text("Only notes you may open", 11, "CockpitTextFaintBrush");
        Content = new StackPanel { Spacing = 8, Width = 340, Margin = new(14, 12), Children = { header, new ScrollViewer { Content = _rows, MaxHeight = 360, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, footer } };
        AttachedToVisualTree += (_, _) =>
        {
            _model.Changed += _OnChanged;
            _Render();
        };
        DetachedFromVisualTree += (_, _) => _model.Changed -= _OnChanged;
    }

    private void _OnChanged(DepotNotesPage page) => Dispatcher.UIThread.Post(_Render);

    private void _Render()
    {
        var live = _model.Snapshot().Where(page => page.Notes.Count > 0).ToList();
        if (live.Count == 0)
        {
            // The status line disappears with the last note, and the popup has nothing left to anchor to.
            _close();
            return;
        }

        _rows.Children.Clear();
        foreach (var page in live)
        {
            foreach (var note in page.Notes)
            {
                _rows.Children.Add(_Row(page.ConnectionId, note));
            }
        }
    }

    private Control _Row(string connectionId, DepotLiveNote note)
    {
        var who = note.People.Count == 0 ? null : string.Join(", ", note.People.Select(person => person.Name));
        var title = note.Title.Length > 0 ? note.Title : note.Path;
        var text = new StackPanel { Spacing = 1, Children = { _Text(title, 12.5, weight: FontWeight.SemiBold), _Text(note.Project, 11, "CockpitTextSecondaryBrush") } };
        if (who is not null)
        {
            text.Children.Add(_Text(who, 11, "CockpitTextSecondaryBrush"));
        }

        var dot = _Text("●", 12.5, "CockpitStatusDoneBrush");
        dot.VerticalAlignment = VerticalAlignment.Top;
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Children = { dot, text } };
        Grid.SetColumn(text, 1);
        var button = new Button { Content = row, Classes = { "Ghost", "Compact" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(button, who is null ? $"Open live note {title}, {note.Project}" : $"Open live note {title}, {note.Project}, with {who}");
        button.Click += async (_, _) =>
        {
            // Read before closing: a closed popup is off the tree and has no top level.
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            _close();
            await _OpenAsync(connectionId, note, launcher).ConfigureAwait(true);
        };
        return button;
    }

    private async Task _OpenAsync(string connectionId, DepotLiveNote note, ILauncher? launcher)
    {
        try
        {
            if (_connections().FirstOrDefault(connection => connection.Id == connectionId) is not { } connection)
            {
                _host.ShowToast("That note is no longer available.", PluginToastSeverity.Warning);
                return;
            }

            await _model.JoinAsync(connection, note, uri =>
            {
                if (launcher is null)
                {
                    _host.ShowToast("Could not open that note in the browser.", PluginToastSeverity.Warning);
                    return Task.CompletedTask;
                }

                return launcher.LaunchUriAsync(uri);
            }).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot: opening a live note from the status bar failed: {exception}");
            _host.ShowToast("Could not open that note. It may no longer be available.", PluginToastSeverity.Warning);
        }
    }

    private static TextBlock _Text(string text, double size, string brush = "CockpitTextPrimaryBrush", FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brush));
        return block;
    }
}
