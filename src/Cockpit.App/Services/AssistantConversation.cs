using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;

namespace Cockpit.App.Services;

// AC-1375: `IAssistantConversation` over the assistant's own session host. The question row is built here because
// it is a view model; the card's JSON arrives already composed, in the native AskUserQuestion tool's own shape.
internal sealed class AssistantConversation(IAssistantSessionHost host) : IAssistantConversation, ISingletonService
{
    // No UI-thread hop: the host's own gate reads properties already published off the UI thread by its
    // property-changed subscription.
    public bool RequestConversationClear() => host.RequestConversationClear();

    // AC-955: the assistant's own session via the host, not the grid — the assistant sits outside it by design.
    public Task<bool> ShowQuestionAsync(string question, string inputJson) => ShowQuestionAsync(question, inputJson, []);

    // Assistant threads (§ 9.4): a question about one or more projects waits in each of their threads.
    public Task<bool> ShowQuestionAsync(string question, string inputJson, IReadOnlyList<string> projectIds) =>
        UiThreadCall.RunAsync(() =>
        {
            if (host.View() is not { } session)
            {
                return false;
            }

            session.AddQuestion(question, inputJson, projectIds);
            return true;
        });

    public Task<AssistantThreadResult> SetThreadAsync(IReadOnlyList<string> projectIds) =>
        UiThreadCall.RunAsync(() => host.View() is { } session ? session.SetTurnThreads(projectIds) : _NotRunning);

    public Task<AssistantThreadResult> TagPromptAsync(IReadOnlyList<(string ProjectId, string Quote)> parts) =>
        UiThreadCall.RunAsync(() => host.View() is { } session ? session.TagLastPrompt(parts) : _NotRunning);

    public Task<IReadOnlyList<AssistantThreadInfo>> ListThreadsAsync() =>
        UiThreadCall.RunAsync<IReadOnlyList<AssistantThreadInfo>>(() => host.View() is { } session ? session.SummarizeThreads() : []);

    public Task<string?> WhyThreadCannotCloseAsync(string projectId) =>
        UiThreadCall.RunAsync(() => host.View() is { } session
            ? session.SummarizeThreads().Any(thread => thread.ProjectId == projectId)
                ? session.IsThreadClosed(projectId) ? "That thread is closed already." : session.WhyThreadCannotClose(projectId)
                : "There is no thread for that project id; take it from list_threads."
            : _NotRunning.Note);

    public Task<AssistantThreadResult> SetThreadClosedAsync(string projectId, bool closed) =>
        UiThreadCall.RunAsync(() => host.View() is { } session
            ? closed ? session.CloseThread(projectId) : session.ReopenThread(projectId)
            : _NotRunning);

    public Task<AssistantThreadResult> SuggestClosingThreadsAsync(IReadOnlyList<string> projectIds) =>
        UiThreadCall.RunAsync(() => host.View() is { } session ? session.SuggestClosingThreads(projectIds) : _NotRunning);

    private static readonly AssistantThreadResult _NotRunning = new(false, "My own session is not running.");
}
