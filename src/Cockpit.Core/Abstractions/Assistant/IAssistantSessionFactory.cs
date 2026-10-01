namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// Makes the assistant's own session, on no desk and outside the registry's <c>All</c> (AC-1379, AC-1439).
/// The desktop makes its window's pane until AC-1440 moves that window onto the contracts.
/// </summary>
public interface IAssistantSessionFactory
{
    /// <summary>
    /// The assistant's session as the registry's <c>Assistant</c>; null when this cockpit cannot start sessions.
    /// The caller starts it and holds the only reference.
    /// </summary>
    IAssistantSession? CreateAssistantSession();

    /// <summary>
    /// Lets go of an assistant session its host stood down; the registry forgets it only while it is still the one held.
    /// </summary>
    void ReleaseAssistantSession(IAssistantSession session);
}
