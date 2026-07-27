using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Usage;

/// <summary>
/// Appends the usage trail (AC-251) to <c>usage-history.jsonl</c> next to <c>cockpit.json</c>. The append-only,
/// never-throws, JSON-per-line machinery — and the tail-read that keeps the last N without loading the whole
/// file — lives in <see cref="JsonlAuditLog{T}"/>; this only names the file.
/// <para>
/// Deliberately not a section of <c>cockpit.json</c>: that file is settings, and every write to it rewrites the
/// whole document. A record per turn belongs in something that is appended to, and appending is also what makes
/// the trail survive the crash that loses an in-memory meter.
/// </para>
/// </summary>
internal sealed class UsageHistoryLog : JsonlAuditLog<UsageSnapshot>, IUsageHistory, ISingletonService
{
    public UsageHistoryLog(ILogger<UsageHistoryLog> logger)
        : base(_DefaultPath(), logger)
    {
    }

    /// <summary>Test seam: point the trail at an arbitrary file.</summary>
    internal UsageHistoryLog(string logFilePath, ILogger<UsageHistoryLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "usage";

    // Nothing here is free text, so there is nothing to trim before writing.
    protected override UsageSnapshot PrepareForWrite(UsageSnapshot entry) => entry;

    private static string _DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default) ?? string.Empty, "usage-history.jsonl");
}
