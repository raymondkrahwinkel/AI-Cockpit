namespace Cockpit.Core.Abstractions.Assistant;

public sealed record SessionMcpSetResult(bool Ok, string? Manager, IReadOnlyList<SessionMcpServerState> Servers, string? Error);
