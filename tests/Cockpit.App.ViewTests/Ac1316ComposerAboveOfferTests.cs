using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-1316: one composer with two hosts, measured on the full CockpitView the way the operator meets it.
[Collection("avalonia")]
public class Ac1316ComposerAboveOfferTests
{
    // While the offer stands, the box is inside the offer's own scroll stack, not docked under the transcript's
    // place. Measured on where it hangs in the visual tree rather than on a flag.
    [Fact]
    public void WhileTheOfferStands_TheComposerStandsInsideIt_NotAtTheFoot() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("simple-view-start-screen-empty");
        try
        {
            window.UpdateLayout();

            var input = _Named<TextBox>(window, "InputBox");
            var ancestors = input.GetVisualAncestors().ToList();

            Assert.Contains(ancestors, a => a is ScrollViewer { Name: "StartOffer" });
            Assert.DoesNotContain(ancestors, a => a is ContentControl { Name: "BottomComposerHost" });

            var column = window.GetVisualDescendants().OfType<AssistantChatView>().First(v => v.IsEffectivelyVisible);
            var top = input.TranslatePoint(new Point(0, 0), column)!.Value.Y;
            Assert.True(top < column.Bounds.Height / 2, $"the box stands in the upper half of the column, not as a strip at the bottom (top {top:0} of {column.Bounds.Height:0})");
        }
        finally
        {
            window.Close();
        }
    });

    // The first message sent from the offer: the same box — not a second one — is now at the foot of the column,
    // the offer is gone, and the caret is still in it. Measured on the empty-conversation door: the scene's
    // `+ New session` door rebuilds the whole column when the request stands down, so it cannot show the move.
    [Fact]
    public async Task TheFirstMessage_MovesTheSameComposerToTheFoot_AndKeepsTheCaret() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var window = Screenshotter.ShowScene("simple-view-start-screen");
        try
        {
            var cockpit = (CockpitViewModel)window.DataContext!;
            var conversation = cockpit.AssistantChat!.Session!;
            conversation.Transcript.Clear();
            cockpit.SimpleStartScreenRequested = false;
            window.UpdateLayout();

            var input = _Named<TextBox>(window, "InputBox");
            var offer = _Named<ScrollViewer>(window, "StartOffer");
            Assert.Contains(input.GetVisualAncestors(), a => ReferenceEquals(a, offer));

            input.Focus();
            var chat = (AssistantChatViewModel)input.DataContext!;
            chat.InputText = "what is still open on AC-1316?";
            await chat.SendCommand.ExecuteAsync(null);
            // What the real host does with a sent message: it lands in the transcript, and that is what takes
            // the offer away (the scene's host sends nowhere).
            conversation.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "what is still open on AC-1316?"));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();

            Assert.False(offer.IsVisible);
            Assert.Same(input, window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "InputBox"));
            Assert.Contains(input.GetVisualAncestors(), a => a is ContentControl { Name: "BottomComposerHost" });
            Assert.DoesNotContain(input.GetVisualAncestors(), a => ReferenceEquals(a, offer));
            Assert.True(input.IsFocused, "the caret follows the box to its new host");
        }
        finally
        {
            window.Close();
        }
    });

    // With the assistant switched off, the offer's notice carries a button onto the cockpit's own Options deep-link
    // — the same command the pairing banner uses — and the screenshot button, whose command would be null with
    // no session to take the image, is not on screen at all.
    [Fact]
    public void WhileTheAssistantIsOff_TheNoticeOpensOptions_AndTheDeadScreenshotButtonIsGone() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("simple-view-start-screen-off");
        try
        {
            window.UpdateLayout();
            var cockpit = (CockpitViewModel)window.DataContext!;

            var button = _Named<Button>(window, "OpenAssistantOptionsButton");
            Assert.True(button.IsEffectivelyVisible, "the notice offers the way in, not a route to walk");
            Assert.Contains(button.GetVisualAncestors(), a => a is ScrollViewer { Name: "StartOffer" });
            Assert.Same(cockpit.OpenAssistantOptionsCommand, button.Command);

            var screenshot = _Named<Button>(window, "ScreenshotButton");
            Assert.False(screenshot.IsEffectivelyVisible, "a button with a null command is not offered");
        }
        finally
        {
            window.Close();
        }
    });

    // Typing stays on while the assistant is off (retyping is the retry route after a failed start), so the box
    // changes only what it invites: the action to take first, not the F10 invitation.
    [Fact]
    public void WhileTheAssistantIsOff_TheBoxNamesTheActionFirst_AndStillTakesTyping() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("simple-view-start-screen-off");
        try
        {
            window.UpdateLayout();
            var input = _Named<TextBox>(window, "InputBox");

            Assert.Equal("Turn the assistant on first, or start on a project below.", input.PlaceholderText);
            Assert.True(input.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    private static T _Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
}
