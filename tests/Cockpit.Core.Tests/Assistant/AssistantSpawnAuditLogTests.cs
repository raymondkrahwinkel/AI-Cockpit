using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Infrastructure.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// The assistant spawn trail (AC-545, criterion 5): every session the assistant asked the host to start or stop,
/// on disk rather than only in the transcript of whichever conversation happened to be open at the time.
/// </summary>
public class AssistantSpawnAuditLogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    public AssistantSpawnAuditLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "assistant-spawn-audit.jsonl");
    }

    [Fact]
    public async Task RecordedEntries_SurviveInTheFile_AndComeBackNewestFirst()
    {
        var log = new AssistantSpawnAuditLog(_logPath, NullLogger<AssistantSpawnAuditLog>.Instance);

        await log.RecordAsync(_Entry(AssistantSpawnAction.Start, "pane-1"));
        await log.RecordAsync(_Entry(AssistantSpawnAction.Stop, "pane-1"));

        // A second instance: the trail is on disk, not in memory.
        var reopened = new AssistantSpawnAuditLog(_logPath, NullLogger<AssistantSpawnAuditLog>.Instance);
        var entries = await reopened.ReadRecentAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal(AssistantSpawnAction.Stop, entries[0].Action);
        Assert.Equal(AssistantSpawnAction.Start, entries[1].Action);
    }

    [Fact]
    public async Task AHandEditedOrHalfWrittenLine_IsSkipped_RatherThanLosingTheWholeTrail()
    {
        // The base class's behaviour (JsonlAuditLog<T>), pinned here rather than re-tested: a corrupt line does
        // not take the rest of the trail down with it.
        await File.WriteAllTextAsync(_logPath, "{ this is not json\n");
        var log = new AssistantSpawnAuditLog(_logPath, NullLogger<AssistantSpawnAuditLog>.Instance);
        await log.RecordAsync(_Entry(AssistantSpawnAction.Start, "pane-1"));

        var entries = await log.ReadRecentAsync();

        var entry = Assert.Single(entries);
        Assert.Equal(AssistantSpawnAction.Start, entry.Action);
    }

    private static AssistantSpawnAuditEntry _Entry(AssistantSpawnAction action, string? paneId) => new(
        DateTimeOffset.Now,
        action,
        SpawnCaller.Assistant,
        CallerPaneId: null,
        WorkspaceId: "workspace-1",
        WorkspaceName: "Review",
        Profile: "claude-sonnet",
        WorkingDirectory: @"C:\repo",
        PaneId: paneId,
        SessionName: "AC-223",
        Refusal: null);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
