namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>Named outcomes for <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/> — see each factory for what produces it.</summary>
public enum ProjectMemorySourceLocationsOutcome
{
    // Failed is deliberately the zero value — an unstubbed fake or a missed switch arm must never read as a usable
    // list, the same defensive reasoning PluginMcpSignInOutcome's own doc comment gives for its own zero value.

    /// <summary>The source could not answer — see <see cref="ProjectMemorySourceLocationsResult.Error"/>. Never a bare empty list on failure (AC-502 criterion 5).</summary>
    Failed,

    /// <summary>The source answered with <see cref="ProjectMemorySourceLocationsResult.Locations"/>, possibly empty.</summary>
    Success,

    /// <summary>The source needs a sign-in before it can list anything (AC-502 criterion 4) — offer <see cref="ProjectMemorySourceRegistration.SignInAsync"/>.</summary>
    AuthorizationRequired,
}

/// <summary>One call's result for <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/>.</summary>
public sealed record ProjectMemorySourceLocationsResult(
    ProjectMemorySourceLocationsOutcome Outcome,
    IReadOnlyList<ProjectMemorySourceLocation> Locations,
    string? Error)
{
    public static ProjectMemorySourceLocationsResult Success(IReadOnlyList<ProjectMemorySourceLocation> locations) =>
        new(ProjectMemorySourceLocationsOutcome.Success, locations, null);

    public static ProjectMemorySourceLocationsResult AuthorizationRequired { get; } =
        new(ProjectMemorySourceLocationsOutcome.AuthorizationRequired, [], null);

    public static ProjectMemorySourceLocationsResult Failed(string error) =>
        new(ProjectMemorySourceLocationsOutcome.Failed, [], error);
}
