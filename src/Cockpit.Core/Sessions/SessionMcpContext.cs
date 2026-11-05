using Cockpit.Core.Projects;

namespace Cockpit.Core.Sessions;

public sealed record SessionMcpContext(
    Project? Project,
    string WorkingDirectory,
    string? ConfineRoot,
    string? AgentName,
    SessionMcpManager Manager = SessionMcpManager.Operator);
