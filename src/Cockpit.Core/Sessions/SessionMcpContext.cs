using Cockpit.Core.Projects;

namespace Cockpit.Core.Sessions;

public sealed record SessionMcpContext(
    Project? Project,
    string WorkingDirectory,
    string? ConfineRoot,
    string? AgentName,
    SessionMcpManager Manager = SessionMcpManager.Operator)
{
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    // AC-2220: the brain the start resolved, so its memory source can add a query parameter to one of its own servers.
    public string? Brain { get; init; }
}
