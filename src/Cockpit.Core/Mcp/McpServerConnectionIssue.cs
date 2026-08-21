namespace Cockpit.Core.Mcp;

// A server this session's route tried to mount but never got tools from (AC-997) — unreachable, crashed at
// start, or (AC-500) still waiting on an OAuth sign-in. Reason is always one line: no stack trace, no stderr
// tail, so it is safe to show the operator as-is.
public sealed record McpServerConnectionIssue(string Name, string Reason);
