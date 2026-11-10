using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.UI;

// The pop-out of the "Live note" item, after the companion-strook mockup (AC-2190): the live notes as tabs, the host's
// live editor over the picked one in a fixed-height well, and a foot with where it lives and whether new notes open
// by themselves — or a way to open the note where editing works.
internal sealed class DepotLiveNoteView : UserControl
{
    // The mockup's pop-out is 400 wide; the host adds its body padding round this.
    private const double ViewWidth = 370;
    private const double NoteHeight = 190;

    private readonly ICockpitUiHost _host;
    private readonly DepotNotesModel _notes;
    private readonly Func<IReadOnlyList<DepotConnectionRegistration>> _connections;
    private readonly ICompanionToolContext _context;
    private readonly WrapPanel _tabs = new();
    private readonly CheckBox _auto = new() { Content = "Open new notes automatically", Classes = { "Switch" }, FontSize = 11 };
    private readonly TextBlock _where = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _body = new();
    private List<(string ConnectionId, DepotLiveNote Note)> _items = [];
    private int _selected = -1;
    private ILiveMarkdownEditor? _editor;
    private string? _shown;

    public DepotLiveNoteView(ICockpitUiHost host, DepotNotesModel notes, Func<IReadOnlyList<DepotConnectionRegistration>> connections, ICompanionToolContext context)
    {
        (_host, _notes, _connections, _context) = (host, notes, connections, context);
        Width = ViewWidth;
        _auto.IsChecked = context.Storage.Get<bool?>(DepotLiveNoteTool.AutoOpenKey) != false;
        _auto.IsCheckedChanged += (_, _) => context.Storage.Set(DepotLiveNoteTool.AutoOpenKey, _auto.IsChecked == true);
        _where.Bind(TextBlock.ForegroundProperty, _where.GetResourceObservable("CockpitTextSecondaryBrush"));
        _auto.Bind(ForegroundProperty, _auto.GetResourceObservable("CockpitTextSecondaryBrush"));

        // The mockup's .seg: pill tabs in an inset track.
        var track = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(2), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = _tabs };
        track.Bind(Border.BackgroundProperty, track.GetResourceObservable("CockpitInsetBgBrush"));
        track.Bind(Border.BorderBrushProperty, track.GetResourceObservable("CockpitHairlineBrush"));

        var saved = new MaterialIcon { Kind = MaterialIconKind.CloudCheckOutline, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        saved.Bind(MaterialIcon.ForegroundProperty, saved.GetResourceObservable("CockpitStatusDoneBrush"));
        DockPanel.SetDock(saved, Dock.Left);
        DockPanel.SetDock(_auto, Dock.Right);
        _where.Margin = new Thickness(6, 0, 8, 0);
        var foot = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Child = new DockPanel { Children = { saved, _auto, _where } },
        };
        foot.Bind(Border.BorderBrushProperty, foot.GetResourceObservable("CockpitHairlineSoftBrush"));

        Content = new StackPanel { Spacing = 8, Children = { track, _body, foot } };
        AttachedToVisualTree += (_, _) =>
        {
            _notes.Changed += _OnChanged;
            _Fill();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _notes.Changed -= _OnChanged;
            _Leave();
        };
    }

    private void _OnChanged(DepotNotesPage page) => Dispatcher.UIThread.Post(_Fill);

    private void _Fill()
    {
        _items = [.. _notes.Snapshot().SelectMany(page => page.Notes.Select(note => (page.ConnectionId, note)))];
        var followed = DepotLiveNoteTool.Followed(_notes, _context.Storage.Get<string>(DepotLiveNoteTool.NoteKey));
        _selected = followed is { } pick ? _items.FindIndex(item => item == pick) : -1;
        _PaintTabs();
        var key = followed is { } now ? DepotLiveNoteTool.KeyOf(now.ConnectionId, now.Note) : null;
        if (_body.Content is null || key != _shown)
        {
            _Show();
        }
    }

    private void _PaintTabs()
    {
        _tabs.Children.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var (connectionId, note) = _items[index];
            var tab = new Button { Content = note.Title.Length > 0 ? note.Title : note.Path, Classes = { "Segment" } };
            tab.Classes.Set("on", index == _selected);
            ToolTip.SetTip(tab, $"{note.Title} · {note.Project}");
            var picked = index;
            tab.Click += (_, _) =>
            {
                _context.Storage.Set(DepotLiveNoteTool.NoteKey, DepotLiveNoteTool.KeyOf(connectionId, note));
                _selected = picked;
                _PaintTabs();
                _Show();
            };
            _tabs.Children.Add(tab);
        }

        ((Control)_tabs.Parent!).IsVisible = _items.Count > 0;
    }

    private void _Leave()
    {
        _editor?.Dispose();
        _editor = null;
        _body.Content = null;
        _shown = null;
    }

    private void _Show()
    {
        _Leave();
        if (_selected < 0)
        {
            _where.Text = "Live notes · Depot";
            _body.Content = _Text("No live notes right now. When one starts, it opens here so you can see who is typing.");
            return;
        }

        var (connectionId, note) = _items[_selected];
        _shown = DepotLiveNoteTool.KeyOf(connectionId, note);
        _where.Text = $"Live in Depot · {note.Project}";
        var file = DepotFileRefs.Create(connectionId, note.Address, note.Path);
        if (_host.CreateLiveMarkdownEditor(new LiveMarkdownEditorOptions(DepotMarkdownFiles.WorkspaceTypeId, file)) is { } editor)
        {
            _editor = editor;
            // The mockup's .note: a fixed-height well on the window colour; the note scrolls inside it.
            var well = new Border
            {
                Height = NoteHeight,
                ClipToBounds = true,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                Child = new ScrollViewer { Content = editor.View, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            };
            well.Bind(Border.BackgroundProperty, well.GetResourceObservable("CockpitWindowBgBrush"));
            well.Bind(Border.BorderBrushProperty, well.GetResourceObservable("CockpitHairlineSoftBrush"));
            _body.Content = well;
            return;
        }

        // A host without the live editor: who is in the note, and the note's own tab.
        var open = new Button { Content = "Open to edit", Classes = { "Compact", "Accent" }, HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += async (_, _) =>
        {
            if (_connections().FirstOrDefault(connection => connection.Id == connectionId) is { } connection)
            {
                await _notes.JoinAsync(connection, note, _ => Task.CompletedTask).ConfigureAwait(true);
            }
        };
        var who = note.People.Count == 0 ? "Nobody is in this note." : string.Join(", ", note.People.Select(person => person.Name));
        _body.Content = new StackPanel { Spacing = 8, Children = { _Text(who), _Text("Editing here needs a newer Cockpit."), open } };
    }

    private static TextBlock _Text(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("CockpitTextSecondaryBrush"));
        return block;
    }
}
