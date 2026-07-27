using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// Where a prompt goes when the operator picks one without stopping to fill anything in (AC-53). Shared by the
/// quick-insert palette and the dashboard widget so the two cannot drift on the one question that matters: what
/// happens when there is no session to insert into.
/// </summary>
internal static class PromptInjection
{
    /// <summary>
    /// Sends the template's body to the active session, or puts it on the clipboard when there is none — so the
    /// gesture always produces something rather than failing silently at the one moment the operator is not looking
    /// at a session.
    /// </summary>
    /// <remarks>
    /// The body goes in as written, <c>{{variable}}</c> placeholders and all. Filling those in is the full Prompt
    /// Library dialog's job; the surfaces that use this are the one-click ones, and stopping them for a form would
    /// make them something else.
    /// </remarks>
    internal static Task SendAsync(ICockpitActions actions, PromptTemplate template) =>
        actions.HasActiveSession
            ? actions.InjectIntoActiveSessionAsync(template.Body)
            : actions.SetClipboardTextAsync(template.Body);
}
