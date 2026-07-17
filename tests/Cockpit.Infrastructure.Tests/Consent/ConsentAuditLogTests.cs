using Cockpit.Core.Abstractions.Consent;
using Cockpit.Infrastructure.Consent;
using FluentAssertions;
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

        recent.Select(entry => entry.Scope).Should().Equal("second", "first");
    }

    [Fact]
    public async Task RecordAsync_LongActionText_IsTrimmed()
    {
        var log = CreateLog();
        var longAction = new string('x', 500);
        await log.RecordAsync(Entry("scope") with { ActionText = longAction });

        var recent = await log.ReadRecentAsync();

        recent.Should().ContainSingle();
        recent[0].ActionText.Length.Should().BeLessThan(longAction.Length);
        recent[0].ActionText.Should().EndWith("…");
    }

    [Fact]
    public async Task RecordAsync_UnwritablePath_DoesNotThrow()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"consent-block-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        try
        {
            // A path *under* a regular file: the directory can never be created, so the append fails — and must be
            // swallowed to a warning rather than taking the caller down.
            var log = new ConsentAuditLog(Path.Combine(blockingFile, "sub", "consent-audit.jsonl"), NullLogger<ConsentAuditLog>.Instance);

            var act = async () => await log.RecordAsync(Entry("scope"));

            await act.Should().NotThrowAsync();
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task ReadRecentAsync_MalformedLine_IsSkipped()
    {
        var log = CreateLog();
        await log.RecordAsync(Entry("valid"));
        await File.AppendAllTextAsync(_path, "{ this is not valid json" + Environment.NewLine);

        var recent = await log.ReadRecentAsync();

        recent.Should().ContainSingle().Which.Scope.Should().Be("valid");
    }

    [Fact]
    public async Task ReadRecentAsync_NoFile_ReturnsEmpty()
    {
        var recent = await CreateLog().ReadRecentAsync();

        recent.Should().BeEmpty();
    }
}
