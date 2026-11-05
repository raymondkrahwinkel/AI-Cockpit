namespace Cockpit.Core.Sessions;

public sealed record SessionMcpSet(SessionMcpContext? Context, IReadOnlySet<string> ServerNames);
