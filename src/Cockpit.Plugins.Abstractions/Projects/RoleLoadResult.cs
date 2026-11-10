namespace Cockpit.Plugins.Abstractions.Projects;

public sealed record RoleLoadResult(RoleSourceOutcome Outcome, string? Instructions = null);
