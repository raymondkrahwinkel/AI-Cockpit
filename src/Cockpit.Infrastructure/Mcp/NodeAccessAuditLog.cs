using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Mcp;

// AC-1351: one line per refused attempt, tool call, or key issued/revoked at the node door. A prefix goes only in
// `KeyPrefix` (the caller's key) and `SubjectPrefix` (the key issued or revoked), never part of an unmatched secret.
// AC-1459: for a lifted lockout `SubjectPrefix` is the address. AC-1446: `Actor` is the calling key's label.
internal sealed record NodeAccessAuditEntry(DateTimeOffset At, string Credential, string? KeyPrefix, string RemoteAddress, string? Tool, string Outcome, string? SubjectPrefix = null, string? Actor = null)
{
    public static NodeAccessAuditEntry By(NodeCaller caller, DateTimeOffset at, string? tool, string outcome, string? subject = null) =>
        new(at, caller.Credential, caller.KeyPrefix, caller.RemoteAddress, tool, outcome, subject, caller.ByConnectKey ? caller.Label : null);
}

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
