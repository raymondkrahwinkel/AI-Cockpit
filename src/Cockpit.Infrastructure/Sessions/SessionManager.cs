using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Owns every live driver-backed session. Both an interactive panel and a delegated task (#67) create their
/// session here and stop it here, so "stop" is one path with one resulting state — rather than a UI close flow
/// and an MCP stop_task each tearing a session down their own way.
/// </summary>
internal sealed class SessionManager : ISessionManager, ISingletonService
{
    private readonly ISessionDriverFactory _driverFactory;
    private readonly IWorktreeManager? _worktreeManager;
    private readonly List<ISessionRuntime> _sessions = [];
    private readonly Lock _sessionsLock = new();

    // The worktree manager is optional only so the runtime's own tests can new this up without it; the container
    // always injects the registered singleton (a registered service fills the parameter regardless of its default),
    // so a real session never runs without worktree isolation available.
    public SessionManager(ISessionDriverFactory driverFactory, IWorktreeManager? worktreeManager = null)
    {
        _driverFactory = driverFactory;
        _worktreeManager = worktreeManager;
    }

    public IReadOnlyList<ISessionRuntime> Sessions
    {
        get
        {
            lock (_sessionsLock)
            {
                return _sessions.ToArray();
            }
        }
    }

    public event Action? SessionsChanged;

    public ISessionRuntime Create(SessionProfile? profile)
    {
        var runtime = new SessionRuntime(_driverFactory, profile, _worktreeManager);
        lock (_sessionsLock)
        {
            _sessions.Add(runtime);
        }

        SessionsChanged?.Invoke();
        return runtime;
    }

    public ISessionRuntime? Find(string id)
    {
        lock (_sessionsLock)
        {
            return _sessions.FirstOrDefault(session => session.Id == id);
        }
    }

    public async Task StopAsync(string id)
    {
        ISessionRuntime? runtime;
        lock (_sessionsLock)
        {
            runtime = _sessions.FirstOrDefault(session => session.Id == id);
            if (runtime is null)
            {
                // Already stopped — a close flow and a stop_task can race for the same session, and the loser
                // must not throw.
                return;
            }

            _sessions.Remove(runtime);
        }

        SessionsChanged?.Invoke();
        await runtime.DisposeAsync();
    }
}
