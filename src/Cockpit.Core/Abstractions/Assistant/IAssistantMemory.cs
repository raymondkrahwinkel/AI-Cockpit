namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// What the voice assistant was told to hold on to across sessions (AC-595) — a name, a house rule, which desk
/// "prod" means. Read at every start and appended to while it is talking.
/// </summary>
/// <remarks>
/// <b>Append, never rewrite.</b> Two things said an hour apart are two things, and a memory that replaces itself
/// would quietly lose the first one. Pruning is the operator opening the file: there is no forget tool and no UI,
/// which is a known ceiling rather than an oversight — see the <c>ponytail:</c> note on the implementation.
/// </remarks>
public interface IAssistantMemory
{
    /// <summary>What has been remembered, ready to go into the launch instruction. Empty when nothing ever was.</summary>
    Task<string> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a line. Blank text is refused rather than stored — an empty memory entry is noise a later read cannot tell from a real one.</summary>
    Task RememberAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the conversation stands right now (AC-596) — what carries across the restart the assistant makes when
    /// its context has grown too big. Empty when it has not said.
    /// </summary>
    Task<string> ReadCurrentStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the current state. Unlike <see cref="RememberAsync"/> this overwrites: a current state that
    /// accumulated would be a transcript, which is the thing the restart exists to get rid of.
    /// </summary>
    Task NoteCurrentStateAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes whichever of the memory and current-state files exist to a .zip at <paramref name="archivePath"/>
    /// (AC-657) — a loose, light export separate from a full cockpit backup. Returns the file names written.
    /// </summary>
    Task<IReadOnlyList<string>> ExportAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts back whichever of the two files <paramref name="archivePath"/> carries (AC-657). What is replaced is
    /// copied aside with a timestamp first, never deleted. Returns the file names restored.
    /// </summary>
    Task<IReadOnlyList<string>> ImportAsync(string archivePath, CancellationToken cancellationToken = default);
}
