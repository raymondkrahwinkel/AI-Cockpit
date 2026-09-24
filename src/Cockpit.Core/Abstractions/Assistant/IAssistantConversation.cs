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
}
