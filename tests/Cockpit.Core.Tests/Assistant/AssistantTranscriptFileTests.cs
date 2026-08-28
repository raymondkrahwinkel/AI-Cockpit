using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// The assistant's transcript snapshot (AC-684): what the operator saw survives a save/load round trip, and a
/// machine that never saved one, or a file it cannot make sense of, still starts.
/// </summary>
public class AssistantTranscriptFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public AssistantTranscriptFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "assistant-transcript.json");
    }

    // AC-1151: a zero debounce window so these round-trip tests keep seeing an awaited `SaveAsync` land on disk
    // immediately, same as before debouncing existed — the debounce window itself has its own tests.
    private AssistantTranscriptFile CreateStore() =>
        new(_filePath, NullLogger<AssistantTranscriptFile>.Instance, TimeSpan.Zero);

    [Fact]
    public async Task NothingWasEverSaved_ReadsAsEmpty_RatherThanFailing()
    {
        var store = CreateStore();

        Assert.Empty(await store.LoadAsync());
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public async Task ASavedTranscript_ReadsBackInOrder_IncludingADivider()
    {
        var store = CreateStore();
        var entries = new[]
        {
            new AssistantTranscriptSnapshotEntry("UserText", "fix the layout bug", null, null, null, null, false, DateTimeOffset.Now),
            new AssistantTranscriptSnapshotEntry("Divider", "Context was full — a new conversation starts here", null, null, null, null, false, DateTimeOffset.Now),
            new AssistantTranscriptSnapshotEntry(
                "ToolUse", "", "Bash", """{"command":"ls"}""", "tool-1", "file.txt", false, DateTimeOffset.Now),
        };

        await store.SaveAsync(entries);
        var loaded = await store.LoadAsync();

        Assert.Equal(entries, loaded);
    }

    [Fact]
    public async Task ASecondSave_ReplacesTheFirst_RatherThanAppending()
    {
        // The transcript is a snapshot of where the conversation stands, not a trail — the whole point of AC-684's
        // "overwrite, do not append" choice (mirrors AssistantMemoryFile.NoteCurrentStateAsync).
        var store = CreateStore();
        await store.SaveAsync([new AssistantTranscriptSnapshotEntry("UserText", "first", null, null, null, null, false, DateTimeOffset.Now)]);
        await store.SaveAsync([new AssistantTranscriptSnapshotEntry("UserText", "second", null, null, null, null, false, DateTimeOffset.Now)]);

        var loaded = await store.LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("second", loaded[0].Text);
    }

    [Fact]
    public async Task AFileThatCannotBeParsed_ReadsAsEmpty_RatherThanFailing()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(_filePath, "not json");

        Assert.Empty(await store.LoadAsync());
    }

    // ── Archiving (AC-947) ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archiving_MovesTheCurrentFileToATimestampedPreviousGeneration()
    {
        var store = CreateStore();
        await store.SaveAsync([new AssistantTranscriptSnapshotEntry("UserText", "before the crash", null, null, null, null, false, DateTimeOffset.Now)]);

        await store.ArchiveAsync();

        Assert.False(File.Exists(_filePath));
        var archived = Assert.Single(Directory.GetFiles(_tempDir, "assistant-transcript.previous-*.json"));
        var loaded = await new AssistantTranscriptFile(archived, NullLogger<AssistantTranscriptFile>.Instance).LoadAsync();
        Assert.Equal("before the crash", Assert.Single(loaded).Text);
    }

    [Fact]
    public async Task ArchivingWithNothingEverSaved_DoesNothing()
    {
        var store = CreateStore();

        await store.ArchiveAsync();

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task MoreThanThreeArchives_KeepsOnlyTheThreeNewest()
    {
        // Four pre-existing generations, named so a plain sort orders them oldest to newest — cheaper than
        // driving the clock to produce five real archives one second apart.
        foreach (var stamp in new[] { "20260101-000000", "20260102-000000", "20260103-000000", "20260104-000000" })
        {
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"assistant-transcript.previous-{stamp}.json"), "[]");
        }
        var store = CreateStore();
        await store.SaveAsync([new AssistantTranscriptSnapshotEntry("UserText", "newest", null, null, null, null, false, DateTimeOffset.Now)]);

        await store.ArchiveAsync();

        var remaining = Directory.GetFiles(_tempDir, "assistant-transcript.previous-*.json");
        Assert.Equal(3, remaining.Length);
        Assert.DoesNotContain(remaining, path => path.Contains("20260101-000000", StringComparison.Ordinal));
        Assert.DoesNotContain(remaining, path => path.Contains("20260102-000000", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
