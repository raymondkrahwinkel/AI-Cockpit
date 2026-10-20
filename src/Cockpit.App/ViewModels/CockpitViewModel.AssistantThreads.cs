using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

// What the floating card beside the dock strip says, and the row it opens.
public sealed record AssistantFloatingCardContent(string Title, string Body, TranscriptEntryViewModel Row);

// Assistant threads (§ 7.2): the card is the host's, since it hangs at the strip rather than in the chat.
public partial class CockpitViewModel
{
    [ObservableProperty]
    private AssistantFloatingCardContent? _assistantFloatingCard;

    // Who shows the assistant decides how; this only says which row the operator wants to see.
    public event Action<TranscriptEntryViewModel>? AssistantFloatingCardOpened;

    [RelayCommand]
    private void OpenAssistantFloatingCard()
    {
        if (AssistantFloatingCard is { } card)
        {
            AssistantFloatingCard = null;
            AssistantFloatingCardOpened?.Invoke(card.Row);
        }
    }

    [RelayCommand]
    private void DismissAssistantFloatingCard() => AssistantFloatingCard = null;

    // The Simple stand has no dock tab and no sidebar chip, so its rail row carries the count; a click on it picks the
    // assistant there, in its own column, and opens Questions for you rather than moving the conversation anywhere.
    [RelayCommand]
    private void OpenAssistantQuestions(SessionPanelViewModel? assistant)
    {
        if (assistant is not null)
        {
            SelectSimpleSession(assistant);
        }

        AssistantChat?.OpenQuestionsCommand.Execute(null);
    }
}
