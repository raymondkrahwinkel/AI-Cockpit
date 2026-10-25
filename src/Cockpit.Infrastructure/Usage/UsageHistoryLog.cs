using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Usage;

// Appends the usage trail (AC-251) to `usage-history.jsonl`, not a `cockpit.json` section, so it survives a
// crash that loses an in-memory meter (append-only/tail-read machinery lives in `JsonlAuditLog{T}`). Unlike
// consent/delegation, this trail grows fastest, so size-bounded rollover (AC-399) lives here, not the shared base.
internal sealed class UsageHistoryLog : JsonlAuditLog<UsageSnapshot>, IUsageHistory, ISingletonService
{
    // The live file rolls once it reaches this size. A named constant rather than a bare number so the trade-off
    // (disk footprint vs. how far back "recent usage" can reach) is visible at the call site and in one place to
    // change. 8 MB holds a comfortable multi-month tail of one-line-per-turn JSON before it rolls.
    internal const long MaxSizeBytes = 8 * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly string _liveFilePath;
    private readonly long _maxSizeBytes;

    // Reads (not writes) from the rollover file, so ReadRecentAsync doesn't appear to truncate history right
    // after a rollover. Null on the instance built to *be* that reader, which stops the chain going forever.
    private readonly UsageHistoryLog? _rolloverLog;

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
        _maxSizeBytes = maxSizeBytes;
        _rolloverLog = buildRolloverReader
            ? new UsageHistoryLog(RolloverPathFor(logFilePath), logger, maxSizeBytes, buildRolloverReader: false)
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

    // Rolls the file over (if at or past `MaxSizeBytes`) and appends, as one operation under the shared `TrailsLock`
    // so a rename can't land between another caller's size check and its append. Hides (base method isn't
    // virtual by design) `JsonlAuditLog{T}.RecordAsync`; `IUsageHistory.RecordAsync` dispatch resolves here.
    public new async Task RecordAsync(UsageSnapshot entry, CancellationToken cancellationToken = default)
    {
        // AC-1719: the lock retention shares, so neither a roll nor a fold lands between a size check and its append.
        await SessionDayRetention.TrailsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _RollIfOverLimitAsync().ConfigureAwait(false);
            await base.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SessionDayRetention.TrailsLock.Release();
        }
    }

    // Reads the live file, then continues into the rollover file if that didn't fill `limit` — otherwise
    // usage would look like it dropped to zero right after every rotation. Consent/delegation need none of
    // this since they never roll, so their inherited `JsonlAuditLog{T}.ReadRecentAsync` already reads everything.
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

    private async Task _RollIfOverLimitAsync()
    {
        try
        {
            var info = new FileInfo(_liveFilePath);
            if (!info.Exists || info.Length < _maxSizeBytes)
            {
                return;
            }

            // One generation beyond the live file, as before; what it held is folded into day aggregates first (AC-1719).
            await SessionDayRetention.RollAsync<UsageSnapshot>(_liveFilePath, snapshot => snapshot.RecordedAt, _logger).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Matches the base class's own stance: a broken trail must not take the turn it is measuring down with
            // it. Worst case here, the live file keeps growing past the limit until a rollover succeeds later.
            _logger.LogWarning(ex, "Could not roll over the usage audit log at {Path}.", _liveFilePath);
        }
    }
}
