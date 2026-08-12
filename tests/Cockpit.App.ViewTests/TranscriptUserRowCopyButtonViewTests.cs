using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.ViewTests;

// AC-745: the assistant reply already carried a hover copy button (rowActions); the user's own
// message bubble did not. Pins that the button now renders on a user row and actually copies
// entry.Text, in SessionView.
[Collection("avalonia")]
public class TranscriptUserRowCopyButtonViewTests
{
    [Fact]
    public void AUserRow_ShowsACopyButtonThatCopiesItsText() => HeadlessAvalonia.Run(() =>
    {
        const string text = "remember to check the deploy logs";
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, text));

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
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
