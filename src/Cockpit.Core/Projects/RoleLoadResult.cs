namespace Cockpit.Core.Projects;

public sealed record RoleLoadResult(RoleSourceOutcome Outcome, string? Instructions = null);
