using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The assistant chat window used to always-follow the tail: any scroll up snapped straight back down, so an
/// operator could never read back through a reply while it streamed. It now sticks to the bottom the same way
/// SessionView does (AC-528) — wheeling up stops the follow and shows the jump-to-newest chevron, rows arriving
/// while scrolled up do not drag the view down, and the chevron resumes the follow.
/// </summary>
[Collection("avalonia")]
public sealed class AssistantChatWindowStickToBottomTests
{
    [Fact]
    public async Task WheelingUpWhileStreaming_StaysPut_AndTheChevronResumesFollowing()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var session = new SessionViewModel();
            session.Transcript.Clear();

            // Enough rows to overflow the viewport, so there is history to scroll up into.
            for (var i = 0; i < 40; i++)
            {
                session.Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.AssistantText, $"row {i} of the conversation so far."));
            }

            var host = Substitute.For<IAssistantSessionHost>();
            host.Session.Returns(session);

            var window = new AssistantChatWindow
            {
                Width = 420,
                Height = 520,
                DataContext = new AssistantChatViewModel(
                    host,
                    Substitute.For<IAssistantSettingsStore>(),
                    Substitute.For<IVoicePlaybackQueue>()),
            };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(200);

            try
            {
                var scroll = window.ChatView.TranscriptScroll;
                var chevron = window.GetVisualDescendants().OfType<Button>()
                    .First(button => button.Name == "ScrollToBottomButton");

                // Parked at the tail to begin with: following, so the chevron is hidden.
                Assert.False(chevron.IsVisible, "starts parked at the newest row, so nothing to jump to");

                // The operator wheels up to read back.
                for (var tick = 0; tick < 3; tick++)
                {
                    window.MouseWheel(new Point(window.Width / 2, window.Height / 3), new Vector(0, 1));
                    window.UpdateLayout();
                    await Task.Delay(30);
                }

                Assert.True(chevron.IsVisible, "wheeling up must stop the follow and offer the jump-to-newest chevron");

                // Rows keep streaming in while they read — the view must not be dragged down with them.
                var resting = scroll.Offset.Y;
                for (var streamed = 0; streamed < 5; streamed++)
                {
                    session.Transcript.Add(new TranscriptEntryViewModel(
                        TranscriptEntryKind.AssistantText, $"streamed row {streamed}"));
                    window.UpdateLayout();
                    await Task.Delay(30);
                }

                Assert.Equal(resting, scroll.Offset.Y);
                Assert.True(chevron.IsVisible, "content arriving is not the operator returning to the tail");

                // The chevron jumps back to the newest row and resumes the follow, hiding itself.
                chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                await Task.Delay(100);

                Assert.False(chevron.IsVisible, "the chevron resumes following and hides itself");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
