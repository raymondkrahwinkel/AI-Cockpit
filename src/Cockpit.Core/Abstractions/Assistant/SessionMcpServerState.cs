namespace Cockpit.Core.Abstractions.Assistant;

public sealed record SessionMcpServerState(string Name, string Status, string? Reason = null, bool CanRemove = true);

public interface ISessionMcpSetClient
{
    Task<SessionMcpSetResult> GetMcpSetAsync(CancellationToken cancellationToken = default);

    Task<SessionMcpSetResult> RemoveMcpServerAsync(string name, CancellationToken cancellationToken = default);
}
