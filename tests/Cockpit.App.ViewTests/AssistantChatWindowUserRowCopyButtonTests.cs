using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Material.Icons;
using Material.Icons.Avalonia;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-745: TranscriptRowView is shared with SessionView (AC-722), so this mirrors
// TranscriptUserRowCopyButtonViewTests for the assistant pop-out window — verified rather than
// assumed, since AC-715 found this window carrying its own copy of a row for the consent card.
[Collection("avalonia")]
public sealed class AssistantChatWindowUserRowCopyButtonTests
{
    [Fact]
    public void AUserRow_ShowsACopyButtonThatCopiesItsText() => HeadlessAvalonia.Run(() =>
    {
        const string text = "remember to check the deploy logs";
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, text));

        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);

        var window = new AssistantChatWindow
        {
            Width = 420,
            Height = 560,
            DataContext = new AssistantChatViewModel(
                host,
                Substitute.For<IAssistantSettingsStore>(),
                Substitute.For<IVoicePlaybackQueue>()),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var copyButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible
                                   && button.GetVisualDescendants().OfType<MaterialIcon>()
                                       .Any(icon => icon.Kind == MaterialIconKind.ContentCopy));

            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var copied = window.Clipboard?.TryGetTextAsync().GetAwaiter().GetResult();

            Assert.Equal(text, copied);
        }
        finally
        {
            window.Close();
        }
    });
}
