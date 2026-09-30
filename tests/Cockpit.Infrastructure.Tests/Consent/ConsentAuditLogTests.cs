using Cockpit.Core.Abstractions.Consent;
using Cockpit.Infrastructure.Consent;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Consent;

/// <summary>
/// The consent trail has to survive the things that would quietly lose it: a write that fails, a half-written
/// line, a restart. It is append-only by contract, so these tests hold that a decision, once logged, reads back —
/// and that a broken log never takes the operator's action down with it.
/// </summary>
public sealed class ConsentAuditLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"consent-audit-{Guid.NewGuid():N}.jsonl");

    private ConsentAuditLog CreateLog() => new(_path, NullLogger<ConsentAuditLog>.Instance);

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static ConsentAuditEntry Entry(string scope) =>
        new(DateTimeOffset.UtcNow, ConsentAuditAction.Approved, "Workflows", "pane-1", "workflows", scope, "rm -rf /tmp/x", Remembered: false);

    [Fact]
    public async Task ReadRecentAsync_AfterRecording_ReturnsEntriesNewestFirst()
    {
        var log = CreateLog();
        await log.RecordAsync(Entry("first"));
        await log.RecordAsync(Entry("second"));

        var recent = await log.ReadRecentAsync();

        Assert.Equal(new[] { "second", "first" }, recent.Select(entry => entry.Scope));
    }

    [Fact]
    public async Task ReadRecentAsync_MalformedLine_IsSkipped()
    {
        var log = CreateLog();
        await log.RecordAsync(Entry("valid"));
        await File.AppendAllTextAsync(_path, "{ this is not valid json" + Environment.NewLine);

        var recent = await log.ReadRecentAsync();

        Assert.Equal("valid", Assert.Single(recent).Scope);
    }

    /// <summary>
    /// A trail written by a newer build still reads. #AC-575 adds <c>Bypassed</c> to <see cref="ConsentAuditAction"/>,
    /// and a <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> throws on a value it does not know
    /// — which, on a whole-document read, would cost the older build the entire trail rather than the one line it
    /// cannot understand. Held here rather than assumed: it is the property that lets this enum grow at all, and it
    /// lives in <c>JsonlAuditLog</c>'s per-line parse, several files away from the enum it protects.
    /// </summary>
    [Fact]
    public async Task ReadRecentAsync_ALineWithAnActionThisBuildDoesNotKnow_CostsThatLineOnly()
    {
        var log = CreateLog();
        await log.RecordAsync(Entry("before"));
        await File.AppendAllTextAsync(
            _path,
            """{"At":"2026-08-01T10:00:00+00:00","Action":"SomethingAFutureBuildAdded","SourceLabel":"Workflows","PaneId":"pane-1","PluginId":"workflows","Scope":"from-the-future","ActionText":"x","Remembered":false}"""
                + Environment.NewLine);
        await log.RecordAsync(Entry("after"));

        var recent = await log.ReadRecentAsync();

        Assert.Equal(new[] { "after", "before" }, recent.Select(entry => entry.Scope));
    }

    /// <summary>The other half of the same guarantee: the new value itself round-trips, by name.</summary>
    [Fact]
    public async Task RecordAsync_ABypassedDecision_ReadsBackAsBypassed()
    {
        var log = CreateLog();
        await log.RecordAsync(Entry("scope") with { Action = ConsentAuditAction.Bypassed });

        var recent = await log.ReadRecentAsync();

        Assert.Equal(ConsentAuditAction.Bypassed, Assert.Single(recent).Action);
        Assert.Contains("\"Bypassed\"", await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task ReadRecentAsync_BlankAndCorruptLines_AreSkipped_AndDoNotCountTowardTheLimit()
    {
        var log = CreateLog();
        await log.RecordAsync(Entry("first"));
        await File.AppendAllTextAsync(_path, Environment.NewLine);                       // a blank line
        await File.AppendAllTextAsync(_path, "{ half a line" + Environment.NewLine);     // a corrupt line
        await log.RecordAsync(Entry("second"));

        var recent = await log.ReadRecentAsync(limit: 2);

        Assert.Equal(new[] { "second", "first" }, recent.Select(entry => entry.Scope));
    }

}
