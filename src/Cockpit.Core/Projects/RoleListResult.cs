namespace Cockpit.Core.Projects;

public sealed record RoleListResult(RoleSourceOutcome Outcome, IReadOnlyList<SessionRole> Roles);
