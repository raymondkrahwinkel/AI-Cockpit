using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Services;

/// <summary>
/// The directories the cockpit's open sessions are working in — what delegation (#67) treats as allowed without the
/// target profile having to name them: a session that hands work to another profile is already working there itself.
/// <para>
/// Read from the open panes rather than kept as a list of its own: a session's working directory becomes known when
/// its driver reports it, and a stale copy would either refuse a directory a session is plainly in, or keep granting
/// one long after the session that justified it closed.
/// </para>
/// <para>
/// The cockpit is asked for at the moment of the question, not injected: the view model owns the delegated-tasks view,
/// which owns the delegation service, which owns this — so taking it in the constructor closes a circle, and a circle
/// of singletons does not fail with a message about circular dependencies. It deadlocks on the container's lock, and
/// the process that resolved it never exits.
/// </para>
/// </summary>
internal sealed class SessionWorkspaces(IServiceProvider services) : ISessionWorkspaces, ISingletonService
{
    public IReadOnlyList<string> ActiveWorkingDirectories => services.GetRequiredService<CockpitViewModel>().Sessions
        .Select(session => session.WorkingDirectory)
        .Where(directory => !string.IsNullOrWhiteSpace(directory))
        .Select(directory => directory!)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
