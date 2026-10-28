using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AvaloniaEdit.Editing;
using Cockpit.App.Files;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.ViewTests;

// AC-1898: a live file source edits through patches over the real backend channel; undo is the source's.
[Collection("avalonia")]
public sealed class LiveFileWorkspaceTests
{
    private static readonly string Lines = string.Join('\n', Enumerable.Range(1, 12).Select(line => $"line {line} of the note"));

    // Layer 2, view: a remote patch above the caret keeps the caret and selection on their characters; a local edit
    // goes out as one op on the right base revision; Live shows no Save and a green tab dot, and Ctrl+S writes nothing.
    [Fact]
    public Task ARemotePatch_KeepsCaretAndSelection_AndALocalEditIsOneOp() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (source, body, marker, window) = await _OpenAsync();
        try
        {
            var document = body.Editor.TextEditor.Document;
            var line10 = document.GetLineByNumber(10);
            body.Editor.TextEditor.TextArea.Selection = Selection.Create(body.Editor.TextEditor.TextArea, line10.Offset + 2, line10.Offset + 9);
            body.Editor.TextEditor.CaretOffset = line10.Offset + 9;
            var selected = body.Editor.TextEditor.SelectedText;
            var caretContext = document.GetText(body.Editor.TextEditor.CaretOffset - 4, 8);

            var line12 = document.GetLineByNumber(12);
            document.Insert(line12.EndOffset, "!");
            Dispatcher.UIThread.RunJobs();
            var edit = Assert.Single(source.Edits);
            Assert.Equal(1, edit.BaseRev);
            Assert.Equal(new[] { new TextOp(line12.EndOffset - 1, 0, "!") }, edit.Ops);

            source.Remote(document.GetLineByNumber(3).Offset, "REMOTE\n");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(source.Text.ToString(), body.Editor.Text);
            Assert.Equal(caretContext, document.GetText(body.Editor.TextEditor.CaretOffset - 4, 8));
            Assert.Equal(selected, body.Editor.TextEditor.SelectedText);
            Assert.False(document.UndoStack.CanUndo);
            Assert.Equal(WorkspaceMarker.Live, marker());
            Assert.DoesNotContain(body.GetLogicalDescendants().OfType<Button>(), button => button.IsVisible && Equals(button.Content, "Save"));
            body.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, source.Writes);
            Assert.DoesNotContain(body.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text?.Contains("Ctrl+S") == true);
        }
        finally
        {
            window.Close();
        }
    });

    // Layer 2, view (data loss): Ctrl+Z takes back only what was typed here; the remote text that came after stays.
    // Text still on its way when a remote delete removes the word around it survives on both sides, once.
    [Fact]
    public Task Undo_TakesBackOnlyTheOwnEdit_AndKeepsRemoteText() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (source, body, _, window) = await _OpenAsync();
        try
        {
            body.Editor.TextEditor.CaretOffset = body.Editor.TextEditor.Document.GetLineByNumber(5).EndOffset;
            body.Editor.TextEditor.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "abc" });
            Dispatcher.UIThread.RunJobs();
            source.Remote(0, "WEB ");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("line 5 of the noteabc", body.Editor.Text);

            body.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("WEB " + Lines, body.Editor.Text);
            Assert.Equal(body.Editor.Text, source.Text.ToString());

            var hold = new TaskCompletionSource();
            source.Hold = hold.Task;
            var word = body.Editor.Text.IndexOf("line 7", StringComparison.Ordinal);
            body.Editor.TextEditor.CaretOffset = word + 2;
            body.Editor.TextEditor.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "XY" });
            Dispatcher.UIThread.RunJobs();
            source.Remote(word, "line 7".Length, "");
            Dispatcher.UIThread.RunJobs();
            var landed = source.Landed.Task;
            source.Hold = null;
            hold.SetResult();
            await landed.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(source.Text.ToString(), body.Editor.Text);
            Assert.Contains("\nXY of the note\n", body.Editor.Text);
        }
        finally
        {
            window.Close();
        }
    });

    private static async Task<(LiveSource Source, MarkdownFileWorkspace Body, Func<WorkspaceMarker?> Marker, Window Window)> _OpenAsync()
    {
        var source = new LiveSource(Lines);
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        Cockpit.Infrastructure.Files.FileSourceChannel.Register(hub.For("files"), source);
        WorkspaceMarker? marker = null;
        var context = new WorkspaceContext("live", new NoStorage(), NullCockpitSessionObserver.Instance, null) { MarkerSet = set => marker = set };
        var options = new FileWorkspaceOpenOptions(new FileRef("c1", "note", "note.md"), "note", "note.md", "Depot / Work", Edit: true);
        var body = new MarkdownFileWorkspace(context, options, new FileSourceChannelClient(new Channel(hub)), () => Task.FromResult(false));
        var window = new Window { Content = body, Width = 900, Height = 700 };
        window.Show();
        await body.Ready;
        Dispatcher.UIThread.RunJobs();
        return (source, body, () => marker, window);
    }

    private sealed class Channel(PluginChannelHub hub) : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            hub.InvokeAsync("files", action, payload, cancellationToken);
        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => hub.Subscribe("files", name, handler);
    }

    // A room in miniature: one revision per change, and an undo that only takes back the host's own inserts.
    private sealed class LiveSource(string text) : IFileSourceProvider, IFileSource, ILiveFileSource, ILiveFileSession
    {
        private readonly List<(int Offset, int Length)> _own = [];
        public StringBuilder Text { get; } = new(text);
        public long Rev { get; private set; } = 1;
        public List<(long BaseRev, TextOp[] Ops)> Edits { get; } = [];
        public int Writes { get; private set; }
        public Task? Hold { get; set; }
        public TaskCompletionSource Landed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<FileConnection> Connections => [new("c1", "Fake", "memory", false)];
        public FileSourceCapabilities Capabilities => new(true, FileSourceChannel.MaxContentBytes);
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public event EventHandler<LiveFilePatch>? Patched;
        public event EventHandler<LiveFileStatus>? StatusChanged { add { } remove { } }
        public IFileSource Open(string connectionId) => this;
        public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) =>
            Task.FromResult(new FileReadResult(new MemoryStream(Encoding.UTF8.GetBytes(Text.ToString())), "v1") { CanWrite = true });
        public Task<FileWriteResult> WriteAsync(FileRef target, Stream content, string? ifVersion, bool mustNotExist, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.FromResult(new FileWriteResult(FileWriteStatus.Written, "v2"));
        }
        public Task<ILiveFileSession> OpenLiveAsync(FileRef file, CancellationToken cancellationToken) => Task.FromResult<ILiveFileSession>(this);
        public Task<LiveFileSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LiveFileSnapshot(Text.ToString(), Rev, new LiveFileStatus(LiveFileState.Live)));
        public async Task<LiveEditResult> EditAsync(string editId, long baseRev, IReadOnlyList<TextOp> ops, CancellationToken cancellationToken)
        {
            if (Hold is { } hold)
            {
                await hold;
            }
            if (baseRev != Rev)
            {
                return new LiveEditResult(LiveEditStatus.Stale, Rev);
            }
            Edits.Add((baseRev, [.. ops]));
            foreach (var op in ops)
            {
                Text.Remove(op.Offset, op.Length).Insert(op.Offset, op.Text);
                _own.Add((op.Offset, op.Text.Length));
            }
            var landed = Landed;
            Landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            landed.SetResult();
            return new LiveEditResult(LiveEditStatus.Applied, ++Rev);
        }
        public Task UndoAsync(bool redo, CancellationToken cancellationToken)
        {
            var ops = _own.AsEnumerable().Reverse().Select(own => new TextOp(own.Offset, own.Length, "")).ToArray();
            _own.Clear();
            _Change(ops);
            return Task.CompletedTask;
        }
        public void Remote(int offset, string inserted) => Remote(offset, 0, inserted);
        public void Remote(int offset, int removed, string inserted)
        {
            for (var i = 0; i < _own.Count; i++)
            {
                _own[i] = _own[i].Offset >= offset + removed ? (_own[i].Offset + inserted.Length - removed, _own[i].Length) : _own[i];
            }
            _Change([new TextOp(offset, removed, inserted)]);
        }
        private void _Change(TextOp[] ops)
        {
            foreach (var op in ops)
            {
                Text.Remove(op.Offset, op.Length).Insert(op.Offset, op.Text);
            }
            Patched?.Invoke(this, new LiveFilePatch(++Rev, ops, "web"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoStorage : IPluginStorage
    {
        public T? Get<T>(string key) => default;
        public void Set<T>(string key, T value)
        {
        }
    }
}
