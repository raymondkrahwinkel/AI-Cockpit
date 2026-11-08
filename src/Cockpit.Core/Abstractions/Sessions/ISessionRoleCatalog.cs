using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Abstractions.Sessions;

public interface ISessionRoleCatalog
{
    Task<RoleListResult> ListAsync(Project? project, SessionProfile? profile, CancellationToken cancellationToken = default);
}
