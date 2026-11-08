using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;

namespace Cockpit.Plugin.Depot.UI;

// The pop-out of the "Live note" item: pick a live note, then the host's live editor over it, or a way to open it where editing works.
internal sealed class DepotLiveNoteView : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly DepotNotesModel _notes;
    private readonly Func<IReadOnlyList<DepotConnectionRegistration>> _connections;
    private readonly ICompanionToolContext _context;
    private readonly ComboBox _picker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _auto = new() { Content = "Open automatically" };
    private readonly ContentControl _body = new();
    private List<(string ConnectionId, DepotLiveNote Note)> _items = [];
    private ILiveMarkdownEditor? _editor;
    private string? _shown;
    private bool _filling;

    public DepotLiveNoteView(ICockpitUiHost host, DepotNotesModel notes, Func<IReadOnlyList<DepotConnectionRegistration>> connections, ICompanionToolContext context)
    {
        (_host, _notes, _connections, _context) = (host, notes, connections, context);
        _auto.IsChecked = context.Storage.Get<bool?>(DepotLiveNoteTool.AutoOpenKey) != false;
        _auto.IsCheckedChanged += (_, _) => context.Storage.Set(DepotLiveNoteTool.AutoOpenKey, _auto.IsChecked == true);
        _picker.SelectionChanged += (_, _) =>
        {
            if (!_filling && _picker.SelectedIndex >= 0)
            {
                var (connectionId, note) = _items[_picker.SelectedIndex];
                context.Storage.Set(DepotLiveNoteTool.NoteKey, DepotLiveNoteTool.KeyOf(connectionId, note));
                _Show();
            }
        };
        Content = new StackPanel { Spacing = 8, Children = { _picker, _auto, _body } };
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
        _filling = true;
        _picker.ItemsSource = _items.Select(item => item.Note.Title.Length > 0 ? $"{item.Note.Title} · {item.Note.Project}" : item.Note.Path).ToList();
        _picker.SelectedIndex = followed is { } pick ? _items.FindIndex(item => item == pick) : -1;
        _filling = false;
        var key = followed is { } now ? DepotLiveNoteTool.KeyOf(now.ConnectionId, now.Note) : null;
        if (_body.Content is null || key != _shown)
        {
            _Show();
        }
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
        if (_picker.SelectedIndex < 0)
        {
            _body.Content = _Text("No live notes right now.");
            return;
        }

        var (connectionId, note) = _items[_picker.SelectedIndex];
        _shown = DepotLiveNoteTool.KeyOf(connectionId, note);
        var file = DepotFileRefs.Create(connectionId, note.Address, note.Path);
        if (_host.CreateLiveMarkdownEditor(new LiveMarkdownEditorOptions(DepotMarkdownFiles.WorkspaceTypeId, file)) is { } editor)
        {
            _editor = editor;
            _body.Content = editor.View;
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
