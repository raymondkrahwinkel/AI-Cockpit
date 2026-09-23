using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Mcp;

// AC-1351: one line per thing that happened at the node endpoint's door — a refused attempt, a tool call, a key
// issued or revoked. `Credential` is a key's prefix, "pairing", "unknown" or "none"; never any part of a secret
// that did not match a known key, so a mistyped pairing secret cannot end up here. `Subject` is the prefix of the
// key an issue or revoke acted on.
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
