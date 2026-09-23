using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Mcp;

// AC-1351: one line per refused attempt, tool call, or key issued/revoked at the node door. `Credential` is a
// key's prefix, "pairing", "unknown" or "none" — never part of a secret that matched no known key. `Subject` is the
// prefix of the key an issue or revoke acted on.
internal sealed record NodeAccessAuditEntry(DateTimeOffset At, string Credential, string RemoteAddress, string? Tool, string Outcome, string? Subject = null);

internal sealed class NodeAccessAuditLog : JsonlAuditLog<NodeAccessAuditEntry>, ISingletonService
{
    public NodeAccessAuditLog(ILogger<NodeAccessAuditLog> logger)
        : base(AuditTrailFiles.InStateRoot(AuditTrailFiles.NodeAccess), logger)
    {
    }

    // Test seam: point the log at an arbitrary file.
    internal NodeAccessAuditLog(string logFilePath, ILogger<NodeAccessAuditLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "node access";

    protected override NodeAccessAuditEntry PrepareForWrite(NodeAccessAuditEntry entry) => entry;
}
