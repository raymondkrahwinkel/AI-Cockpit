namespace Cockpit.Core.Projects;

// AC-1013: What a session does with one ProjectResource row (AC-483) — three different verbs on the same data
// shape: Memory is read and written back to; Instructions is read and obeyed; Reference is only looked up.
// Without this distinction a session would treat all rows the same, wrongly obeying or read-locking some.
public enum ProjectResourceRole
{
    // A place this project's memory lives — read to recall what it already knows, written back to as the session
    // learns more. Mirrors what `Project.MemoryRef` alone used to mean, back when a project could only
    // ever have one such place.
    Memory,

    // A file of standing instructions this project's sessions read and follow, the way `Project.BehaviorPrompt`
    // already is — except kept in a file the operator maintains separately rather than typed into the project.
    Instructions,

    // Something a session looks up when it needs it — consulted, not obeyed and never written to.
    Reference,
}
