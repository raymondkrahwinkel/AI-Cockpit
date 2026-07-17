namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The conversation an operator chose from a plugin's picker (<see cref="ConversationPickerRegistration"/>): its
/// <paramref name="SessionId"/> to resume, and the <paramref name="WorkingDirectory"/> it originally ran in when
/// the picker knows it. A provider whose history is scoped to a folder — <c>claude</c> keeps a session's
/// transcript under the directory it was started in, and resuming by id elsewhere would not find it — is only
/// resumed correctly when the session starts in that same directory, so the picker hands the location back with
/// the id and the New-session dialog starts the resumed session there.
/// </summary>
/// <param name="SessionId">The chosen conversation's id, as the provider's resume flag expects it.</param>
/// <param name="WorkingDirectory">
/// The directory the conversation ran in, or <see langword="null"/> when the picker cannot tell — a provider that
/// resumes regardless of directory, or a transcript that never recorded one. The dialog then leaves the working
/// directory as the operator set it.
/// </param>
public sealed record PickedConversation(string SessionId, string? WorkingDirectory = null);
