using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Usage;

// Appends the usage trail (AC-251) to `usage-history.jsonl` next to `cockpit.json`. The append-only,
// never-throws, JSON-per-line machinery — and the tail-read that keeps the last N without loading the whole
// file — lives in `JsonlAuditLog{T}`; this only names the file.
//
// Deliberately not a section of `cockpit.json`: that file is settings, and every write to it rewrites the
// whole document. A record per turn belongs in something that is appended to, and appending is also what makes
// the trail survive the crash that loses an in-memory meter.
//
// Unlike the consent and delegation trails, this one is size-bounded (AC-399): a turn writes to it far more
// often than an operator approves a command or a task is delegated, so left append-only forever it is the one
// trail that actually grows to hundreds of MB a year. The base class stays append-only for everyone — the
// consent trail's whole point is that a record, once logged, cannot be erased — so the rollover lives here, one
// layer up, as a size check this class makes before every write rather than a change to the shared machinery.
// At `MaxSizeBytes` the live file is renamed to its ".1" rollover file (`usage-history.1.jsonl`),
// replacing whatever rollover file was already there — one generation of history kept beyond the live file, not
// an unbounded chain of them.
internal sealed class UsageHistoryLog : JsonlAuditLog<UsageSnapshot>, IUsageHistory, ISingletonService
{
    // The live file rolls once it reaches this size. A named constant rather than a bare number so the trade-off
    // (disk footprint vs. how far back "recent usage" can reach) is visible at the call site and in one place to
    // change. 8 MB holds a comfortable multi-month tail of one-line-per-turn JSON before it rolls.
    internal const long MaxSizeBytes = 8 * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly string _liveFilePath;
    private readonly string _rolloverFilePath;
    private readonly long _maxSizeBytes;

    // Reads (not writes) from the rollover file, so ReadRecentAsync can keep answering "recent usage" across a
    // rollover boundary instead of appearing to truncate history the moment a rollover happens. Null on the
    // instance built to *be* that reader (see the private constructor below) — nothing but this one ever asks a
    // rollover reader to look further back than its own file, which is what stops the chain going on forever.
    private readonly UsageHistoryLog? _rolloverLog;

    // Guards the read-size / rename-if-over-limit / append sequence as one unit. The base class's own write lock
    // only serializes its own appends; without a lock at this layer too, one caller could rename the file out from
    // under another that had already decided (a moment earlier) that no rotation was needed and was about to append.
    private readonly SemaphoreSlim _rotationLock = new(1, 1);

    public UsageHistoryLog(ILogger<UsageHistoryLog> logger)
        : this(AuditTrailFiles.InStateRoot(AuditTrailFiles.Usage), logger, MaxSizeBytes)
    {
    }

    // Test seam: point the trail at an arbitrary file.
    internal UsageHistoryLog(string logFilePath, ILogger<UsageHistoryLog> logger)
        : this(logFilePath, logger, MaxSizeBytes)
    {
    }

    // Test seam: a tiny `maxSizeBytes` so a rollover can be exercised without writing 8 MB.
    internal UsageHistoryLog(string logFilePath, ILogger<UsageHistoryLog> logger, long maxSizeBytes)
        : this(logFilePath, logger, maxSizeBytes, buildRolloverReader: true)
    {
    }

    // The single constructor both public shapes above funnel into. buildRolloverReader is false only when this
    // instance is itself being built as another instance's rollover reader — otherwise every instance would build
    // one to read its own rollover file, which would build one of its own, forever.
    private UsageHistoryLog(string logFilePath, ILogger<UsageHistoryLog> logger, long maxSizeBytes, bool buildRolloverReader)
        : base(logFilePath, logger)
    {
        _logger = logger;
        _liveFilePath = logFilePath;
        _rolloverFilePath = RolloverPathFor(logFilePath);
        _maxSizeBytes = maxSizeBytes;
        _rolloverLog = buildRolloverReader
            ? new UsageHistoryLog(_rolloverFilePath, logger, maxSizeBytes, buildRolloverReader: false)
            : null;
    }

    protected override string LogName => "usage";

    // Nothing here is free text, so there is nothing to trim before writing.
    protected override UsageSnapshot PrepareForWrite(UsageSnapshot entry) => entry;

    // `usage-history.jsonl` rolled to `usage-history.1.jsonl` — the file name any rollover-aware reader
    // or startup housekeeping (AC-435) needs to know about, derived from the live path rather than hardcoded so a
    // test pointed at an arbitrary file gets a rollover file next to it, not next to the real one.
    internal static string RolloverPathFor(string logFilePath)
    {
        var directory = Path.GetDirectoryName(logFilePath);
        var stem = Path.GetFileNameWithoutExtension(logFilePath);
        var extension = Path.GetExtension(logFilePath);
        var rolloverName = $"{stem}.1{extension}";
        return string.IsNullOrEmpty(directory) ? rolloverName : Path.Combine(directory, rolloverName);
    }

    // Rolls the file over (if it is at or past `MaxSizeBytes`) and then appends — as one operation
    // under `_rotationLock`, so a rename can never land between another caller's size check and its
    // append. Hides (rather than overrides — the base method is not virtual, by design: nothing about the
    // append-only contract itself may vary per trail) `JsonlAuditLog{T}.RecordAsync`; the interface
    // dispatch that matters (`IUsageHistory.RecordAsync`) resolves to this one.
    public new async Task RecordAsync(UsageSnapshot entry, CancellationToken cancellationToken = default)
    {
        await _rotationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _RollIfOverLimit();
            await base.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _rotationLock.Release();
        }
    }

    // Reads the live file, then — if that alone did not fill `limit` — continues into the
    // rollover file for the rest. "Recent usage history" is what an operator or the spending-ceiling planner
    // wants back regardless of exactly where the live file happens to have last rolled, so a tail-read that
    // simply stopped at the rollover boundary would make usage look like it dropped to zero right after every
    // rotation. The consent and delegation trails need none of this: they never roll, so their inherited
    // `JsonlAuditLog{T}.ReadRecentAsync` already reads everything there is.
    public new async Task<IReadOnlyList<UsageSnapshot>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        var recent = await base.ReadRecentAsync(limit, cancellationToken).ConfigureAwait(false);
        if (recent.Count >= limit || _rolloverLog is null)
        {
            return recent;
        }

        var older = await _rolloverLog.ReadRecentAsync(limit - recent.Count, cancellationToken).ConfigureAwait(false);
        return older.Count == 0 ? recent : [.. recent, .. older];
    }

    private void _RollIfOverLimit()
    {
        try
        {
            var info = new FileInfo(_liveFilePath);
            if (!info.Exists || info.Length < _maxSizeBytes)
            {
                return;
            }

            // Overwrite whatever rollover file already exists — only one generation beyond the live file is kept,
            // per the maintainer's call: bounding disk footprint here matters more than keeping two generations of
            // a trail whose whole value is being *recent*.
            File.Move(_liveFilePath, _rolloverFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Matches the base class's own stance: a broken trail must not take the turn it is measuring down with
            // it. Worst case here, the live file keeps growing past the limit until a rollover succeeds later.
            _logger.LogWarning(ex, "Could not roll over the usage audit log at {Path}.", _liveFilePath);
        }
    }
}
