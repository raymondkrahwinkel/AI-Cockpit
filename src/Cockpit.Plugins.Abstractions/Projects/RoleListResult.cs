namespace Cockpit.Plugins.Abstractions.Projects;

public sealed record RoleListResult(RoleSourceOutcome Outcome, IReadOnlyList<SessionRole> Roles);
