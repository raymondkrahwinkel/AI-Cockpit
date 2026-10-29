using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AvaloniaEdit.Editing;
using Cockpit.App.Files;
using Cockpit.App.Plugins;
using Cockpit.App.Views.Editor;
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
    // Then, on a fresh workspace, the undo checks below.
    [Fact]
    public Task ARemotePatch_KeepsCaretAndSelection_AndALocalEditIsOneOp_AndUndoTakesBackOnlyTheOwnEdit() => HeadlessAvalonia.RunAsync(async () =>
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

        await _Undo_TakesBackOnlyTheOwnEdit_AndKeepsRemoteTextAsync();
        await _ReadOnly_PreservesQueuedTextAsync();
        await _ReadOnlyJoinAsync(LiveReadOnlyReason.SchemaMismatch, "Read only", true);
        await _ReadOnlyJoinAsync(LiveReadOnlyReason.Viewer, "Viewer · read only", false);
        await _OutsideMarks_KeepUntouchedLinesAsync();
        await _Offline_MergesSeparateLinesAsync();
        await _Offline_BlocksOverlappingLinesAsync();
        await _Offline_RevertingToTheInitialTextStillSavesAsync();
        await _Offline_FailedSaveStaysDirtyAfterSignInExpiresAsync();
        await _Offline_NewerFileKeepsAnExplicitReversionAsync();
        await _StaleRoomIsReopenedOnlyAfterSavingAsync();
        await _TypingDuringFreshJoinIsSavedBeforeReplacingTheEditorAsync();
    });

    // Layer 2, view: attribution crosses the file channel and pending typing only clears the line it touches.
    private static async Task _OutsideMarks_KeepUntouchedLinesAsync()
    {
        var (source, body, _, window) = await _OpenAsync();
        var hold = new TaskCompletionSource();
        source.Hold = hold.Task;
        try
        {
            var document = body.Editor.TextEditor.Document;
            document.Insert(1, "mine");
            Dispatcher.UIThread.RunJobs();
            source.MarkOutside(new ExternalTextEdit("Assistant", DateTimeOffset.UtcNow, [new LiveRange(0, source.Text.ToString().IndexOf("line 4", StringComparison.Ordinal) - 1)]));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { 2, 3 }, body.Editor.ExternalMarkedLines);
            document.Insert(document.GetLineByNumber(2).Offset + 1, "typed");
            Assert.Equal(new[] { 3 }, body.Editor.ExternalMarkedLines);
            document.Insert(0, "preface\n");
            Assert.Equal(new[] { 4 }, body.Editor.ExternalMarkedLines);
            body.Editor.MarkExternalEdit(new ExternalTextEdit("Expired", DateTimeOffset.UtcNow.AddMinutes(-11), [new LiveRange(0, 4)]));
            Assert.Equal(new[] { 4 }, body.Editor.ExternalMarkedLines);
        }
        finally
        {
            hold.TrySetResult();
            window.Close();
        }
    }

    // Layer 2, view: stale host operations go to the file, then a fresh session rebuilds the editor.
    private static async Task _StaleRoomIsReopenedOnlyAfterSavingAsync()
    {
        var (source, body, marker, window) = await _OpenAsync(new LiveFileStatus(LiveFileState.Offline));
        try
        {
            body.Editor.TextEditor.Document.Insert(0, "MINE\n");
            var oldRead = new TaskCompletionSource<LiveFileSnapshot>();
            source.ReadReply = oldRead;
            source.PatchGap = 1;
            source.Remote(0, "OLD READ\n");
            Dispatcher.UIThread.RunJobs();
            await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var oldSnapshot = new LiveFileSnapshot(source.Text.ToString(), source.Rev, new LiveFileStatus(LiveFileState.Live));
            source.ReadReply = null;
            source.PatchGap = 0;
            source.Status = source.Status with { State = LiveFileState.Stale, RequiresRejoin = true };
            source.PublishStatus();
            Dispatcher.UIThread.RunJobs();
            body.Editor.TextEditor.Document.Insert(0, "AFTER STALE\n");
            source.FileText += "\nTHEIRS";
            source.FileVersion = "external";
            await body.SaveAsync();
            Assert.Single(source.Edits);
            Assert.Equal(2, source.OpenCount);
            Assert.Equal(1, source.DisposeCount);
            Assert.Equal("AFTER STALE\nMINE\n" + Lines + "\nTHEIRS", source.FileText);
            Assert.Equal(source.FileText, body.Editor.Text);
            Assert.Equal(LiveFileState.Live, body.LiveStatus?.State);
            Assert.False(body.IsDirty);
            // The in-process channel completes inline on the UI thread, so SetResult runs the old continuation before returning.
            oldRead.SetResult(oldSnapshot);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(source.FileText, body.Editor.Text);
            Assert.Equal(LiveFileState.Live, body.LiveStatus?.State);
            Assert.Single(source.Edits);
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view: typing while OpenLive is awaiting its answer cannot be replaced by that answer.
    private static async Task _TypingDuringFreshJoinIsSavedBeforeReplacingTheEditorAsync()
    {
        var (source, body, marker, window) = await _OpenAsync(new LiveFileStatus(LiveFileState.Stale) { RequiresRejoin = true });
        try
        {
            body.Editor.TextEditor.Document.Insert(0, "MINE\n");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.HoldReopen = release.Task;
            var save = body.SaveAsync();
            await source.ReopenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            body.Editor.TextEditor.Document.Insert(0, "DURING JOIN\n");
            release.SetResult();
            await save;
            Assert.True(body.IsDirty);
            Assert.StartsWith("DURING JOIN\nMINE\n", body.Editor.Text);
            source.HoldReopen = null;
            await body.SaveAsync();
            Assert.Equal("DURING JOIN\nMINE\n" + Lines, source.FileText);
            Assert.Equal(source.FileText, body.Editor.Text);
            Assert.Equal(LiveFileState.Live, body.LiveStatus?.State);
            Assert.Empty(source.Edits);
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view: a newer file cannot turn a deliberate reversion into "unchanged" text.
    private static async Task _Offline_NewerFileKeepsAnExplicitReversionAsync()
    {
        var (source, body, marker, window) = await _OpenAsync();
        try
        {
            source.Remote(0, "line 1".Length, "WEB");
            Dispatcher.UIThread.RunJobs();
            source.Status = source.Status with { State = LiveFileState.Offline };
            source.PublishStatus();
            Dispatcher.UIThread.RunJobs();
            source.FileText = "LATEST" + Lines["line 1".Length..];
            source.FileVersion = "external";
            body.Editor.TextEditor.Document.Text = Lines;
            await body.SaveAsync();
            Assert.True(body.HasConflict);
            Assert.True(body.IsDirty);
            Assert.Empty(source.WriteVersions);
            Assert.Equal(Lines, body.Editor.Text);
            body.MergeSourceChanges();
            await body.SaveAsync();
            Assert.Contains("line 1 of the note", source.FileText);
            Assert.Contains("LATEST of the note", source.FileText);
            Assert.False(body.IsDirty);
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view: a previously saved live revision is the fallback base, even when typing restores the initial text.
    private static async Task _Offline_RevertingToTheInitialTextStillSavesAsync()
    {
        var (source, body, marker, window) = await _OpenAsync();
        try
        {
            source.Remote(0, "WEB\n");
            Dispatcher.UIThread.RunJobs();
            source.FileText = "WEB\n" + Lines;
            source.FileVersion = "external";
            source.Status = source.Status with { State = LiveFileState.Offline };
            source.PublishStatus();
            Dispatcher.UIThread.RunJobs();
            body.Editor.TextEditor.Document.Text = Lines;
            Assert.True(body.IsDirty);
            await body.SaveAsync();
            Assert.Equal(Lines, source.FileText);
            Assert.Equal(new[] { "external" }, source.WriteVersions);
            Assert.False(body.IsDirty);
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view: a plugin acknowledgement and a later sign-in failure cannot acknowledge a failed file save.
    private static async Task _Offline_FailedSaveStaysDirtyAfterSignInExpiresAsync()
    {
        var (source, body, marker, window) = await _OpenAsync(new LiveFileStatus(LiveFileState.Offline));
        try
        {
            body.Editor.TextEditor.Document.Insert(0, "MINE\n");
            source.FailWrites = true;
            await body.SaveAsync();
            source.Status = source.Status with { State = LiveFileState.SignInNeeded };
            source.PublishStatus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(body.IsDirty);
            Assert.StartsWith("MINE\n", body.Editor.Text);
            Assert.Equal(Lines, source.FileText);
            Assert.Equal(WorkspaceMarker.Unsaved, marker());
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view: fallback uses the file version, retaining another writer's separate line.
    private static async Task _Offline_MergesSeparateLinesAsync()
    {
        var (source, body, marker, window) = await _OpenAsync(new LiveFileStatus(LiveFileState.Offline));
        try
        {
            body.Editor.TextEditor.Document.Insert(0, "MINE\n");
            Dispatcher.UIThread.RunJobs();
            source.FileText += "\nTHEIRS";
            source.FileVersion = "external";
            Assert.True(body.IsDirty);
            Assert.Equal("1 lines only in this Cockpit", body.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "LocalLineCount").Text);
            await source.Saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("MINE\n" + Lines + "\nTHEIRS", source.FileText);
            Assert.Equal(source.FileText, body.Editor.Text);
            Assert.False(body.HasConflict);
            Assert.False(body.IsDirty);
            Assert.Equal(new[] { "v1", "external" }, source.WriteVersions);
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view: changing the same line writes nothing until the operator resolves the banner.
    private static async Task _Offline_BlocksOverlappingLinesAsync()
    {
        var (source, body, marker, window) = await _OpenAsync(new LiveFileStatus(LiveFileState.Offline));
        try
        {
            body.Editor.TextEditor.Document.Replace(0, "line 1".Length, "MINE");
            Dispatcher.UIThread.RunJobs();
            source.FileText = "THEIRS" + Lines["line 1".Length..];
            source.FileVersion = "external";
            await body.SaveAsync();
            Assert.True(body.HasConflict);
            Assert.True(body.IsDirty);
            Assert.StartsWith("MINE", body.Editor.Text);
            Assert.StartsWith("THEIRS", source.FileText);
            Assert.Equal(new[] { "v1" }, source.WriteVersions);
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view: read-only joins cannot enter editing, still receive remote patches and keep Download.
    private static async Task _ReadOnlyJoinAsync(LiveReadOnlyReason reason, string chip, bool banner)
    {
        var status = new LiveFileStatus(LiveFileState.ReadOnly, "This note uses a newer Depot editor.") { ReadOnlyReason = reason };
        var (source, body, marker, window) = await _OpenAsync(status);
        try
        {
            Assert.True(body.Editor.IsReadOnly);
            body.BeginEdit();
            body.Editor.TextEditor.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "NO" });
            body.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();
            Assert.True(body.Editor.IsReadOnly);
            Assert.False(body.Editor.ShowSource);
            Assert.False(body.IsDirty);
            Assert.Empty(source.Edits);
            Assert.Equal(0, source.Writes);
            Assert.Equal(chip, body.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "FileStatus").Text);
            Assert.Equal(banner, body.GetLogicalDescendants().OfType<Border>().Single(border => border.Name == "LiveReadOnlyBanner").IsVisible);
            Assert.Contains(body.GetLogicalDescendants().OfType<Button>(), button => button.IsVisible && Equals(button.Content, "Download"));
            Assert.DoesNotContain(body.GetLogicalDescendants().OfType<CheckBox>(), check => check.IsVisible && Equals(check.Content, "Markdown source"));
            source.Remote(0, "WEB ");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("WEB " + Lines, body.Editor.Text);
            Assert.Empty(source.Edits);
            source.Status = status with { UnsentSourceText = "Shared text before the offline edit" };
            source.PublishStatus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(body.IsDirty);
            Assert.True(body.HasConflict);
            Assert.Equal(WorkspaceMarker.Unsaved, marker());
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view (data loss): a refused edit and text queued behind it stay dirty while remote text still arrives.
    private static async Task _ReadOnly_PreservesQueuedTextAsync()
    {
        var (source, body, marker, window) = await _OpenAsync();
        try
        {
            source.Refuse = true;
            var readOnlyApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            body.Editor.PropertyChanged += (_, change) => _ = change.Property == MarkdownEditor.IsReadOnlyProperty
                && body.Editor.IsReadOnly && readOnlyApplied.TrySetResult();
            var hold = new TaskCompletionSource();
            source.Hold = hold.Task;
            body.Editor.TextEditor.Document.Insert(0, "MINE ");
            body.Editor.TextEditor.Document.Insert(5, "QUEUED ");
            hold.SetResult();
            await source.Refused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await readOnlyApplied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();
            Assert.True(body.Editor.IsReadOnly);
            Assert.True(body.IsDirty);
            Assert.Equal(WorkspaceMarker.Unsaved, marker());
            Assert.StartsWith("MINE QUEUED ", body.Editor.Text);
            Assert.True(body.HasConflict);
            body.BeginEdit();
            Assert.True(body.Editor.IsReadOnly);
            source.Remote(Lines.Length, "\nWEB");
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("MINE QUEUED ", body.Editor.Text);
            Assert.EndsWith("\nWEB", body.Editor.Text);
            Assert.True(body.IsDirty);
            body.ToggleCompare();
            Assert.Contains(body.GetLogicalDescendants().OfType<Button>(), button => button.IsVisible && Equals(button.Content, "Copy your text"));
            await body.SaveAsync();
            Assert.Equal(0, source.Writes);
            Assert.Empty(source.Edits);
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, view (data loss): Ctrl+Z takes back only what was typed here; the remote text that came after stays.
    // Text still on its way when a remote delete removes the word around it survives on both sides, once.
    private static async Task _Undo_TakesBackOnlyTheOwnEdit_AndKeepsRemoteTextAsync()
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
    }

    private static async Task<(LiveSource Source, MarkdownFileWorkspace Body, Func<WorkspaceMarker?> Marker, Window Window)> _OpenAsync(LiveFileStatus? status = null)
    {
        var source = new LiveSource(Lines) { Status = status ?? new LiveFileStatus(LiveFileState.Live) };
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
        public string FileText { get; set; } = text;
        public string FileVersion { get; set; } = "v1";
        public List<string?> WriteVersions { get; } = [];
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailWrites { get; set; }
        public int OpenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Task? HoldReopen { get; set; }
        public TaskCompletionSource ReopenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LiveFileSnapshot>? ReadReply { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PatchGap { get; set; }
        public Task? Hold { get; set; }
        public bool Refuse { get; set; }
        public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LiveFileStatus Status { get; set; } = new(LiveFileState.Live);
        public TaskCompletionSource Landed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<FileConnection> Connections => [new("c1", "Fake", "memory", false)];
        public FileSourceCapabilities Capabilities => new(true, FileSourceChannel.MaxContentBytes);
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public event EventHandler<LiveFilePatch>? Patched;
        public void MarkOutside(ExternalTextEdit edit) => Patched?.Invoke(this, new LiveFilePatch(++Rev, []) { ExternalEdit = edit });
        public event EventHandler<LiveFileStatus>? StatusChanged;
        public void PublishStatus() => StatusChanged?.Invoke(this, Status);
        public IFileSource Open(string connectionId) => this;
        public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) =>
            Task.FromResult(new FileReadResult(new MemoryStream(Encoding.UTF8.GetBytes(FileText)), FileVersion) { CanWrite = true });
        public Task<FileWriteResult> WriteAsync(FileRef target, Stream content, string? ifVersion, bool mustNotExist, CancellationToken cancellationToken)
        {
            Writes++;
            WriteVersions.Add(ifVersion);
            if (FailWrites)
            {
                throw new FileSourceException(FileSourceErrorKind.Unavailable, "The fake file source is offline.");
            }
            if (ifVersion != FileVersion)
            {
                return Task.FromResult(new FileWriteResult(FileWriteStatus.Conflict, FileVersion));
            }
            using var reader = new StreamReader(content, leaveOpen: true);
            FileText = reader.ReadToEnd();
            FileVersion = "v2";
            Saved.TrySetResult();
            return Task.FromResult(new FileWriteResult(FileWriteStatus.Written, "v2"));
        }
        public async Task<ILiveFileSession> OpenLiveAsync(FileRef file, CancellationToken cancellationToken)
        {
            OpenCount++;
            if (OpenCount > 1)
            {
                ReopenStarted.TrySetResult();
                if (HoldReopen is { } hold)
                {
                    await hold;
                }
                Text.Clear().Append(FileText);
                Status = Status with { State = LiveFileState.Live, RequiresRejoin = false };
            }
            return this;
        }
        public Task<LiveFileSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            if (ReadReply is { } reply)
            {
                ReadStarted.TrySetResult();
                return reply.Task;
            }
            return Task.FromResult(new LiveFileSnapshot(Text.ToString(), Rev, Status));
        }
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
            if (Refuse)
            {
                Refused.TrySetResult();
                throw new FileSourceException(FileSourceErrorKind.Forbidden, "You can no longer edit this note.");
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
            Patched?.Invoke(this, new LiveFilePatch(Rev += 1 + PatchGap, ops, "web"));
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoStorage : IPluginStorage
    {
        public T? Get<T>(string key) => default;
        public void Set<T>(string key, T value)
        {
        }
    }
}
