using Avalonia.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class CompanionAssistantChatView : UserControl
{
    public CompanionAssistantChatView()
    {
        InitializeComponent();
        Chat.IsCompanion = true;
    }

    internal CompanionAssistantChatView(AssistantChatViewModel chat, Action openInCockpit, Action close)
        : this()
    {
        Chat.DataContext = chat;
        OpenInCockpitButton.Click += (_, _) => openInCockpit();
        CloseButton.Click += (_, _) => close();
    }
}
