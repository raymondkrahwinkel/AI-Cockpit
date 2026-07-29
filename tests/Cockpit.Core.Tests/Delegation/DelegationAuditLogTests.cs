using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Infrastructure.Delegation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The delegation audit trail (#67): what was handed to which profile and how it ended, in a file that outlives
/// the app. The task list only exists while the cockpit runs, so without this "what did the agents do while I was
/// away" is unanswerable.
/// </summary>
public class DelegationAuditLogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    public DelegationAuditLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "delegation-audit.jsonl");
    }

    [Fact]
    public async Task RecordedEntries_SurviveInTheFile_AndComeBackNewestFirst()
    {
        var log = new DelegationAuditLog(_logPath, NullLogger<DelegationAuditLog>.Instance);

        await log.RecordAsync(_Entry(DelegationAuditAction.Delegated, "task-1"));
        await log.RecordAsync(_Entry(DelegationAuditAction.Completed, "task-1"));

        // A second instance: the trail is on disk, not in memory.
        var reopened = new DelegationAuditLog(_logPath, NullLogger<DelegationAuditLog>.Instance);
        var entries = await reopened.ReadRecentAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(entries));
        Assert.Equal(DelegationAuditAction.Completed, entries[0].Action);
        Assert.Equal(DelegationAuditAction.Delegated, entries[1].Action);
    }

    [Fact]
    public async Task ARefusal_IsRecorded_WithTheReason()
    {
        // The interesting half of the trail: what an agent tried, and what stopped it. A log of successes alone
        // would never show that something was turned away.
        var log = new DelegationAuditLog(_logPath, NullLogger<DelegationAuditLog>.Instance);

        await log.RecordAsync(_Entry(DelegationAuditAction.Refused, taskId: null) with
        {
            Reason = "Profile 'private' is not available as a delegation target.",
        });

        var entries = await log.ReadRecentAsync();

        Assert.Single(entries);
        Assert.Equal(DelegationAuditAction.Refused, entries[0].Action);
        Assert.Contains("not available as a delegation target", entries[0].Reason);
    }

    [Fact]
    public async Task ALongPrompt_IsTrimmed_SoTheLogDoesNotBecomeATranscript()
    {
        var log = new DelegationAuditLog(_logPath, NullLogger<DelegationAuditLog>.Instance);

        await log.RecordAsync(_Entry(DelegationAuditAction.Delegated, "task-1") with { Prompt = new string('x', 5_000) });

        var entries = await log.ReadRecentAsync();

        Assert.True(entries[0].Prompt!.Length < 500);
    }

    [Fact]
    public async Task AHandEditedOrHalfWrittenLine_IsSkipped_RatherThanLosingTheWholeTrail()
    {
        await File.WriteAllTextAsync(_logPath, "{ this is not json\n");
        var log = new DelegationAuditLog(_logPath, NullLogger<DelegationAuditLog>.Instance);
        await log.RecordAsync(_Entry(DelegationAuditAction.Completed, "task-1"));

        var entries = await log.ReadRecentAsync();

        var entry = Assert.Single(entries);
        Assert.Equal(DelegationAuditAction.Completed, entry.Action);
    }

    [Fact]
    public async Task ALongPromptEndingInAnAstralChar_IsTrimmedWithoutALoneSurrogate()
    {
        // C5, inherited from the shared base (AC-59): 299 plain chars then an emoji straddling the 300-char cut. The
        // old char-index trim kept the high surrogate and dropped the low one, which round-trips through JSON as
        // U+FFFD; the surrogate-safe trim drops the whole pair instead.
        var log = new DelegationAuditLog(_logPath, NullLogger<DelegationAuditLog>.Instance);

        await log.RecordAsync(_Entry(DelegationAuditAction.Delegated, "task-1") with { Prompt = new string('x', 299) + "😀" });

        var entries = await log.ReadRecentAsync();

        Assert.DoesNotContain("�", entries[0].Prompt);
        Assert.EndsWith("…", entries[0].Prompt);
    }

    [Fact]
    public async Task ReadingABsentLog_ReturnsNothing_RatherThanThrowing()
    {
        var log = new DelegationAuditLog(Path.Combine(_tempDir, "never-written.jsonl"), NullLogger<DelegationAuditLog>.Instance);

        var entries = await log.ReadRecentAsync();

        Assert.Empty(entries);
    }

    private static DelegationAuditEntry _Entry(DelegationAuditAction action, string? taskId) => new(
        DateTimeOffset.Now,
        action,
        ProfileLabel: "local",
        taskId,
        Label: "summarise",
        TaskType: null,
        Prompt: "summarise the changelog",
        Reason: null);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
