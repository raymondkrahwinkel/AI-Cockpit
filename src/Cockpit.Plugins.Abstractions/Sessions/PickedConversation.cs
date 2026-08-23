namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The conversation an operator chose from a plugin's picker (<see cref="ConversationPickerRegistration"/>): its
/// id to resume, and the working directory it originally ran in when the picker knows it.
/// </summary>
/// <remarks>
/// A provider whose history is scoped to a folder (<c>claude</c> keeps a session's transcript under the directory
/// it started in) only resumes correctly when the session starts in that same directory, so the picker hands the
/// location back with the id and the New-session dialog starts the resumed session there.
/// </remarks>
/// <param name="SessionId">
/// The chosen conversation's id, as the provider's resume flag expects it.
/// </param>
/// <param name="WorkingDirectory">
/// The directory the conversation ran in, or <see langword="null"/> when the picker cannot tell — a provider that
/// resumes regardless of directory, or a transcript that never recorded one. The dialog then leaves the working
/// directory as the operator set it.
/// </param>
public sealed record PickedConversation(string SessionId, string? WorkingDirectory = null);
