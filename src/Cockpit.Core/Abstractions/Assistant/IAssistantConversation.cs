namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The assistant's own running conversation, as <c>AssistantAgentGateway</c> acts on it (AC-1375).
/// The seam stands until the assistant's session host itself moves out of the app.
/// </summary>
public interface IAssistantConversation
{
    /// <summary>
    /// Asks for the conversation to be cleared at the next safe moment; false when a clear was already queued.
    /// </summary>
    bool RequestConversationClear();

    /// <summary>
    /// Shows a structured question card in the assistant's own transcript; false when its session is not running.
    /// </summary>
    Task<bool> ShowQuestionAsync(string question, string inputJson);

    /// <summary>
    /// The same card, filed under <paramref name="projectIds"/> in the conversation's threads.
    /// </summary>
    Task<bool> ShowQuestionAsync(string question, string inputJson, IReadOnlyList<string> projectIds) =>
        ShowQuestionAsync(question, inputJson);

    /// <summary>
    /// Files the rest of the current turn under <paramref name="projectIds"/> (set_thread); empty is General.
    /// </summary>
    Task<AssistantThreadResult> SetThreadAsync(IReadOnlyList<string> projectIds) =>
        Task.FromResult(AssistantThreadResult.Unavailable);

    /// <summary>
    /// Splits the operator's last prompt over projects by quoting each part (tag_prompt).
    /// </summary>
    Task<AssistantThreadResult> TagPromptAsync(IReadOnlyList<(string ProjectId, string Quote)> parts) =>
        Task.FromResult(AssistantThreadResult.Unavailable);

    /// <summary>
    /// Every project thread, open or closed (list_threads).
    /// </summary>
    Task<IReadOnlyList<AssistantThreadInfo>> ListThreadsAsync() => Task.FromResult<IReadOnlyList<AssistantThreadInfo>>([]);

    /// <summary>
    /// Why a thread cannot close right now, or null when it can.
    /// </summary>
    Task<string?> WhyThreadCannotCloseAsync(string projectId) => Task.FromResult<string?>(AssistantThreadResult.Unavailable.Note);

    /// <summary>
    /// Closes or reopens a project's thread.
    /// </summary>
    Task<AssistantThreadResult> SetThreadClosedAsync(string projectId, bool closed) => Task.FromResult(AssistantThreadResult.Unavailable);

    /// <summary>
    /// Puts the one suggestion row asking the operator to close these threads.
    /// </summary>
    Task<AssistantThreadResult> SuggestClosingThreadsAsync(IReadOnlyList<string> projectIds) => Task.FromResult(AssistantThreadResult.Unavailable);
}
