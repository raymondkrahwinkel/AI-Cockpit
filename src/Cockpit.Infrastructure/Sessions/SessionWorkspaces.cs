using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1013: directories open sessions are working in (delegation #67's allow-list), read live from the panes rather
// than cached so it never grants a directory whose session already closed. AC-1373: read from the session registry,
// grid panes only, the same set `CockpitViewModel.Sessions` held.
internal sealed class SessionWorkspaces(ISessionRegistry sessions) : ISessionWorkspaces, ISingletonService
{
    public IReadOnlyList<string> ActiveWorkingDirectories => sessions.All
        .Where(session => !session.IsEmbedded)
        .Select(session => session.WorkingDirectory)
        .Where(directory => !string.IsNullOrWhiteSpace(directory))
        .OfType<string>()
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public string? WorkingDirectoryForPane(string paneId) =>
        sessions.Find(paneId) is { IsEmbedded: false } session ? session.WorkingDirectory : null;
}
