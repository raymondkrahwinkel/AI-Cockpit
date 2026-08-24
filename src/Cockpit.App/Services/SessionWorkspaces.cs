using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Services;

// AC-1013: directories open sessions are working in (delegation #67's allow-list), read live from the panes rather
// than cached so it never grants a directory whose session already closed. Resolved from the container at call time
// rather than injected, since the ownership chain makes constructor injection a circular singleton dependency.
internal sealed class SessionWorkspaces(IServiceProvider services) : ISessionWorkspaces, ISingletonService
{
    public IReadOnlyList<string> ActiveWorkingDirectories => services.GetRequiredService<CockpitViewModel>().Sessions
        .Select(session => session.WorkingDirectory)
        .Where(directory => !string.IsNullOrWhiteSpace(directory))
        .Select(directory => directory!)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public string? WorkingDirectoryForPane(string paneId) => services.GetRequiredService<CockpitViewModel>().Sessions
        .FirstOrDefault(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal))?
        .WorkingDirectory;
}
