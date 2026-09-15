namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// Named outcomes for <see cref="ProjectMemorySourceRegistration.AppendNoteAsync"/> (AC-492) — see each factory for what produces it.
/// </summary>
public enum ProjectMemoryAppendOutcome
{
    // Failed is deliberately the zero value — an unstubbed fake or a missed switch arm must never read as "the
    // note landed", the same defensive reasoning ProjectMemorySourceLocationsOutcome gives for its own zero value.

    /// <summary>
    /// The note did not land — see <see cref="ProjectMemoryAppendResult.Error"/>. The caller still holds the note and should say so rather than drop it.
    /// </summary>
    Failed,

    /// <summary>
    /// The note was appended after whatever the location already held.
    /// </summary>
    Success,

    /// <summary>
    /// The source needs a sign-in before it can write — offer <see cref="ProjectMemorySourceRegistration.SignInAsync"/> and retry with the same note.
    /// </summary>
    AuthorizationRequired,
}
