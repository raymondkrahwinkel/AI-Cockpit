using System.Threading.Channels;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Notes.Live;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1901, layer 2 (contract, dataverlies): Cockpit as the room's seeder and saver (protocol section 6) against a fake Depot that
// answers note-save as Depot does (the base must be the file, own = the session's last save), and a real Y.Doc in Jint.
public class DepotNoteSaverTests
{
    private static readonly DepotConnectionRegistration Connection = new("work", "Work", "https://work.example.com");
    private static readonly DepotNoteLiveRequest Note = new("work", "acme", "roadmap", "notes/plan.md");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AloneInANewNote_CockpitSeedsAndSaves_AndEach409KindKeepsTheFile_SoTextFromOutsideIsMergedNotOverwritten()
    {
        var depot = new _Depot("# Plan\n\nFirst line\n");
        var hub = new _Hub(depot);
        var live = new TaskCompletionSource();
        var saves = Channel.CreateUnbounded<LiveFileSave>();
        LiveFileSave? seen = null;
        await using var session = new DepotNoteSession(
            Connection, Note, _ => Task.FromResult("user-1"), (_, _, onClosed) => hub.With(onClosed), clientId => new JintNoteRoom(clientId),
            _ => { }, [TimeSpan.Zero], post: depot.PostAsync,
            statusChanged: status =>
            {
                lock (saves)
                {
                    if (status.State == LiveFileState.Live)
                    {
                        live.TrySetResult();
                    }

                    if (status.Saved is { Name: not null } saved && !ReferenceEquals(saved, seen))
                    {
                        seen = saved;
                        saves.Writer.TryWrite(saved);
                    }
                }
            },
            quiet: TimeSpan.Zero, retry: [TimeSpan.Zero]);

        // Alone in an empty room: Cockpit joins as a saver, seeds from the file, and nobody has saved since (mockup 3a).
        session.Connect();
        await live.Task.WaitAsync(Wait);
        Assert.True(hub.Join?.Save);
        Assert.NotEmpty(hub.Updates);
        Assert.Same(LiveFileSave.NotYet, session.Status.Saved);
        Assert.Equal(depot.File, (await session.ReadAsync(CancellationToken.None)).Text);

        // Typing is saved over the file it was written against, and the chip names who saved (mockup 4).
        await _TypeAsync(session, "First line", " one");
        var first = await depot.NextPostAsync();
        Assert.Equal(("# Plan\n\nFirst line one\n", _Depot.Sha("# Plan\n\nFirst line\n")), (first.Content, first.Base));
        var saved = await saves.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(("you", null), (saved.Name, saved.Color));
        Assert.NotNull(saved.At);

        // A lost answer: the save landed, the retry gets a 409 whose checksum is its own text, and that becomes the base.
        depot.LoseNextAnswer = true;
        await _TypeAsync(session, "one", " two");
        var lost = await depot.NextPostAsync();
        var retried = await depot.NextPostAsync();
        Assert.Equal((lost.Content, lost.Base), (retried.Content, retried.Base));
        await saves.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal("# Plan\n\nFirst line one two\n", depot.File);

        // own: a save of this session landed first (the saver before), so it is the base, and this text is saved over it.
        depot.SaveOwn("# Plan\n\nFirst line one tw\n");
        await _TypeAsync(session, "two", " three");
        var refused = await depot.NextPostAsync();
        var over = await depot.NextPostAsync();
        Assert.Equal(_Depot.Sha("# Plan\n\nFirst line one two\n"), refused.Base);
        Assert.Equal(_Depot.Sha("# Plan\n\nFirst line one tw\n"), over.Base);
        await saves.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal("# Plan\n\nFirst line one two three\n", depot.File);

        // A writer outside the room: the 409 holds the saves, the merge keeps both texts, and only then is the file written again.
        depot.WriteOutside("# Plan\n\nFirst line one two three\n\nAgent block\n");
        await _TypeAsync(session, "First", "Zero ", before: true);
        var held = await depot.NextPostAsync();
        var merged = await depot.NextPostAsync();
        Assert.Equal(_Depot.Sha("# Plan\n\nFirst line one two three\n"), held.Base);
        Assert.Equal(_Depot.Sha("# Plan\n\nFirst line one two three\n\nAgent block\n"), merged.Base);
        await saves.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal("# Plan\n\nZero First line one two three\n\nAgent block\n", depot.File);
        Assert.Equal([_Depot.Sha("# Plan\n\nFirst line one two three\n\nAgent block\n")], hub.Merged);
        Assert.Equal(depot.File, (await session.ReadAsync(CancellationToken.None)).Text);
        Assert.Equal(7, depot.Posts);
    }

    // Types `text` right after (or before) the first `anchor` in the session's text, on its current revision.
    private static async Task _TypeAsync(DepotNoteSession session, string anchor, string text, bool before = false)
    {
        var now = await session.ReadAsync(CancellationToken.None);
        var at = now.Text.IndexOf(anchor, StringComparison.Ordinal) + (before ? 0 : anchor.Length);
        var result = await session.EditAsync(Guid.NewGuid().ToString(), now.Rev, [new TextOp(at, 0, text)], CancellationToken.None);
        Assert.Equal(LiveEditStatus.Applied, result.Status);
    }

    // Depot's note-save and Text as NoteSaveEndpoints and NoteSessions answer them: a base that is not the file is a 409, `own` when
    // the file is this session's last save; every stored text is a version Text can hand back.
    private sealed class _Depot(string file)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, string> _versions = new() { [Sha(file)] = file };
        private readonly Channel<(string Content, string Base)> _posts = Channel.CreateUnbounded<(string, string)>();
        private string? _own;
        private int _version = 1;

        public string File { get; private set; } = file;

        public bool LoseNextAnswer { get; set; }

        public int Posts { get; private set; }

        public static string Sha(string text) => DepotNoteSaver.Sha256(text);

        public async Task<(string Content, string Base)> NextPostAsync() => await _posts.Reader.ReadAsync().AsTask().WaitAsync(Wait);

        public Task<DepotNoteSaveAnswer> PostAsync(string content, string baseChecksum, string session, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Posts++;
                _posts.Writer.TryWrite((content, baseChecksum));
                if (baseChecksum != Sha(File))
                {
                    return Task.FromResult(new DepotNoteSaveAnswer(409, Sha(File), Own: Sha(File) == _own));
                }

                _Store(content);
                _own = Sha(content);
                if (LoseNextAnswer)
                {
                    LoseNextAnswer = false;
                    throw new HttpRequestException("The answer got lost.");
                }

                return Task.FromResult(new DepotNoteSaveAnswer(200, Sha(content), ++_version));
            }
        }

        public void SaveOwn(string content)
        {
            lock (_gate)
            {
                _Store(content);
                _own = Sha(content);
            }
        }

        public void WriteOutside(string content)
        {
            lock (_gate)
            {
                _Store(content);
            }
        }

        public DepotNoteText? Text(string? checksum)
        {
            lock (_gate)
            {
                return checksum is null ? new DepotNoteText(Sha(File), File) : _versions.TryGetValue(checksum, out var text) ? new DepotNoteText(checksum, text) : null;
            }
        }

        private void _Store(string content)
        {
            File = content;
            _versions[Sha(content)] = content;
        }
    }

    // The hub of an empty room in which Cockpit is the first Editor: it seeds and saves only when it joins with save=true (§6).
    private sealed class _Hub(_Depot depot) : IDepotNotesHub
    {
        private Func<Exception?, Task>? _closed;

        public DepotNoteJoinRequest? Join { get; private set; }

        public List<byte[]> Updates { get; } = [];

        public List<string> Merged { get; } = [];

        public string? ConnectionId => "connection-1";

        public _Hub With(Func<Exception?, Task> onClosed)
        {
            _closed = onClosed;
            return this;
        }

        public void Subscribe(Func<DepotNoteEvent, Task> onEvent)
        {
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DepotNoteJoinResult> JoinAsync(DepotNoteJoinRequest request, CancellationToken cancellationToken)
        {
            Join = request;
            var file = depot.Text(null) ?? throw new InvalidOperationException("The fake Depot has no file.");
            return Task.FromResult(new DepotNoteJoinResult(
                "doc-1", "editor", [], Seed: request.Save, AwaitingSeed: !request.Save, Epoch: "epoch-1", Stale: false, SchemaMismatch: false,
                Saver: request.Save ? ConnectionId : null, Markdown: file.Markdown)
            {
                Checksum = file.Checksum, SessionId = "session-1",
            });
        }

        public Task UpdateAsync(string documentId, byte[] update, CancellationToken cancellationToken)
        {
            lock (Updates)
            {
                Updates.Add(update);
            }

            return Task.CompletedTask;
        }

        public Task EditedAsync(string documentId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompactAsync(string documentId, byte[] snapshot, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RefillAsync(string documentId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task PresenceAsync(string documentId, byte[] awareness, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DepotNoteText?> TextAsync(string documentId, string? checksum, CancellationToken cancellationToken) => Task.FromResult(depot.Text(checksum));

        public Task MergedAsync(string documentId, string checksum, int[] blocks, int conflicts, CancellationToken cancellationToken)
        {
            Merged.Add(checksum);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => _closed is null ? ValueTask.CompletedTask : new ValueTask(_closed(null));
    }
}
