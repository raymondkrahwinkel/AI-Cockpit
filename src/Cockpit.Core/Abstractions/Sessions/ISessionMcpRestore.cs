using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

public interface ISessionMcpRestore
{
    /// <summary>Restores the desired set and its delegation ceiling before the session binds.</summary>
    Task<IReadOnlySet<string>?> RestoreAsync(SessionRestorePlan plan, CancellationToken cancellationToken = default);
}
