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

    private AssistantTranscriptFile CreateStore() => new(_filePath, NullLogger<AssistantTranscriptFile>.Instance);

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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
