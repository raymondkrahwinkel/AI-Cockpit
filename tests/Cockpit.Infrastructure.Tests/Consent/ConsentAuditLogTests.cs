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
    public async Task RecordAsync_LongActionText_IsTrimmed()
    {
        var log = CreateLog();
        var longAction = new string('x', 500);
        await log.RecordAsync(Entry("scope") with { ActionText = longAction });

        var recent = await log.ReadRecentAsync();

        Assert.Single(recent);
        Assert.True(recent[0].ActionText.Length < longAction.Length);
        Assert.EndsWith("…", recent[0].ActionText);
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

            await act();
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

        Assert.Equal("valid", Assert.Single(recent).Scope);
    }

    [Fact]
    public async Task ReadRecentAsync_NoFile_ReturnsEmpty()
    {
        var recent = await CreateLog().ReadRecentAsync();

        Assert.Empty(recent);
    }

    [Fact]
    public async Task ReadRecentAsync_WithLimit_ReturnsOnlyTheNewestN_NewestFirst()
    {
        var log = CreateLog();
        for (var i = 0; i < 10; i++)
        {
            await log.RecordAsync(Entry(i.ToString()));
        }

        var recent = await log.ReadRecentAsync(limit: 3);

        Assert.Equal(new[] { "9", "8", "7" }, recent.Select(entry => entry.Scope));
    }

    [Fact]
    public async Task ReadRecentAsync_ManyEntriesSpanningReadBlocks_TailsNewestFirst()
    {
        // The trail is read backward a block at a time (16 KB). Well over a block's worth of entries, each padded
        // so a handful of entries already exceed one block, proves a line that straddles a block boundary is
        // reassembled rather than lost or split — and that the newest-first order holds across blocks.
        var log = CreateLog();
        const int total = 400;
        for (var i = 0; i < total; i++)
        {
            await log.RecordAsync(Entry(i.ToString()) with { ActionText = new string('x', 200) });
        }

        var recent = await log.ReadRecentAsync(limit: 250);

        Assert.Equal(250, System.Linq.Enumerable.Count(recent));
        Assert.Equal("399", recent[0].Scope);
        Assert.Equal("150", recent[^1].Scope);
        var scopes = recent.Select(entry => int.Parse(entry.Scope)).ToList();
        Assert.Equal(scopes.OrderByDescending(x => x), scopes);
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

    [Fact]
    public async Task ReadRecentAsync_MultiByteContentAcrossBlocks_RoundTripsWithoutMangling()
    {
        // Splitting the backward read on '\n' (a byte that never appears inside a multi-byte UTF-8 sequence) must
        // keep an emoji or em-dash intact even when its bytes land on a block boundary.
        var log = CreateLog();
        for (var i = 0; i < 200; i++)
        {
            await log.RecordAsync(Entry(i.ToString()) with { ActionText = $"delete {i} — files 😀 {new string('=', 120)}" });
        }

        var recent = await log.ReadRecentAsync(limit: 200);

        Assert.Equal(200, System.Linq.Enumerable.Count(recent));
        Assert.All(recent, entry => Assert.True(entry.ActionText.Contains("😀") && entry.ActionText.Contains("—")));
        Assert.DoesNotContain(recent, entry => entry.ActionText.Contains('�'));
    }

    [Fact]
    public async Task RecordAsync_ActionTextWithAstralCharAtTheLimit_TrimsWithoutALoneSurrogate()
    {
        // 299 plain chars then an emoji whose surrogate pair straddles the 300-char cut. A char-index trim would
        // keep the high surrogate and drop the low one, persisting U+FFFD; the surrogate-safe trim drops the pair.
        var log = CreateLog();
        var action = new string('x', 299) + "😀";
        await log.RecordAsync(Entry("scope") with { ActionText = action });

        var recent = await log.ReadRecentAsync();

        Assert.Single(recent);
        Assert.DoesNotContain("�", recent[0].ActionText);
        Assert.EndsWith("…", recent[0].ActionText);
    }
}
