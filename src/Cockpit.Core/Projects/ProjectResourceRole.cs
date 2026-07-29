namespace Cockpit.Core.Projects;

/// <summary>
/// What a session does with one <see cref="ProjectResource"/> row (AC-483). Three roles, not one, because they name
/// three different verbs a session performs on what is otherwise the same shape of data (a reference and a label):
/// <list type="bullet">
/// <item><description><see cref="Memory"/> is read <em>and</em> written back to — a session goes there to learn
/// what the project already knows, and leaves what it just learned for the next one.</description></item>
/// <item><description><see cref="Instructions"/> is read and obeyed — standing instructions a session follows, the
/// way <see cref="Project.BehaviorPrompt"/> already is, except kept in a file rather than typed into the project
/// itself.</description></item>
/// <item><description><see cref="Reference"/> is looked up, never written to and never obeyed — material a session
/// consults when it needs an answer, the way an <see cref="ProjectInfoField"/> row already is for the operator.</description></item>
/// </list>
/// Without this distinction the host would have to say the same sentence about all three rows ("here is a thing"),
/// and that sentence is wrong for at least two of them: a session that treats reference material as an instruction
/// to obey, or a memory folder as read-only, is doing something the operator never asked for.
/// </summary>
public enum ProjectResourceRole
{
    /// <summary>
    /// A place this project's memory lives — read to recall what it already knows, written back to as the session
    /// learns more. Mirrors what <see cref="Project.MemoryRef"/> alone used to mean, back when a project could only
    /// ever have one such place.
    /// </summary>
    Memory,

    /// <summary>
    /// A file of standing instructions this project's sessions read and follow, the way <see cref="Project.BehaviorPrompt"/>
    /// already is — except kept in a file the operator maintains separately rather than typed into the project.
    /// </summary>
    Instructions,

    /// <summary>Something a session looks up when it needs it — consulted, not obeyed and never written to.</summary>
    Reference,
}
