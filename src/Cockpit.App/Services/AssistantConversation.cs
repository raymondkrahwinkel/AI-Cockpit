using Cockpit.App.ViewModels;
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
    public Task<bool> ShowQuestionAsync(string question, string inputJson) =>
        UiThreadCall.RunAsync(() =>
        {
            if (host.Session is not { } session)
            {
                return false;
            }

            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Question, question)
            {
                InputJson = inputJson,
                QuestionPrompts = AskUserQuestionViewModel.Parse(inputJson),
                IsPendingBrokerAnswer = true,
            });

            return true;
        });
}
