namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// One call's result for <see cref="ProjectMemorySourceRegistration.AppendNoteAsync"/>.
/// </summary>
public sealed record ProjectMemoryAppendResult(ProjectMemoryAppendOutcome Outcome, string? Error)
{
    public static ProjectMemoryAppendResult Success { get; } = new(ProjectMemoryAppendOutcome.Success, null);

    public static ProjectMemoryAppendResult AuthorizationRequired { get; } = new(ProjectMemoryAppendOutcome.AuthorizationRequired, null);

    public static ProjectMemoryAppendResult Failed(string error) => new(ProjectMemoryAppendOutcome.Failed, error);
}
