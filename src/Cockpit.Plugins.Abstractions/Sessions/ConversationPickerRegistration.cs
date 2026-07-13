namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A way to pick an earlier conversation to resume, contributed by a plugin
/// (<see cref="ICockpitHost.AddConversationPicker"/>). The New-session dialog offers to resume a conversation
/// by id, and typing an id by hand is a poor way to find one — but the cockpit itself knows nothing about any
/// provider's history, and should not: the transcripts are one provider's own format. So a plugin that <em>can</em>
/// browse that history registers a picker, and the dialog shows a search button that runs it.
/// </summary>
/// <param name="Title">What the picker does, shown as the button's tooltip, e.g. "Search transcripts".</param>
/// <param name="PickAsync">
/// Runs when the operator asks to pick one — typically opening the plugin's own search dialog. Returns the
/// chosen conversation's id, or <see langword="null"/> when they cancelled without choosing.
/// </param>
public sealed record ConversationPickerRegistration(string Title, Func<Task<string?>> PickAsync);
