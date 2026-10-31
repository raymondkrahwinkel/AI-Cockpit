using Cockpit.App.Composition;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1880, layer 2 (scroll stability): the assistant's conversation scrolled by the operator, in All and in a
/// thread, while a reply streams. The panel guessed every unmeasured row from the mean of the measured ones, so each
/// measurement moved every unseen row above the viewport and the rows it had just realised were arranged off screen:
/// 36-42 of 360 painted frames empty. The threads' one-line status rows beside long replies are what made the mean
/// swing that far. Also holds AC-774's claim for this window: it builds only the rows that fit.
/// </summary>
[Collection("avalonia")]
public sealed class AssistantChatTranscriptScrollTests
{
    [Fact]
    public async Task ScrollingAMixedConversation_NeverPaintsAnEmptyViewport()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var session = new SessionViewModel();
            session.Transcript.Clear();
            session.Threads = new ThreadLookup(id => id == "p1" ? "P1" : null, _ => null, _ => null);
            var sizes = new Random(7);
            for (var i = 0; i < 160; i++)
            {
                string[] projects = i % 3 == 0 ? ["p1"] : [];
                var (kind, text) = (i % 4) switch
                {
                    0 => (TranscriptEntryKind.UserText, $"prompt {i}"),
                    1 => (TranscriptEntryKind.Status, $"status {i}"),
                    _ => (TranscriptEntryKind.AssistantText, string.Join("\n\n", Enumerable.Range(0, sizes.Next(1, 14))
                        .Select(paragraph => $"paragraph {paragraph} of reply {i}, long enough to wrap in a narrow chat"))),
                };
                session.Transcript.Add(new TranscriptEntryViewModel(kind, text) { ProjectIds = projects });
            }

            var host = Substitute.For<IAssistantSessionHost>();
            host.Session.Returns(TestSessions.Assistant(session));
            var window = new AssistantChatWindow
            {
                Width = 420,
                Height = 620,
                DataContext = new AssistantChatViewModel(host, Substitute.For<IAssistantSettingsStore>(), Substitute.For<IVoicePlaybackQueue>()),
            };
            window.Show();
            await AssistantChatScrolling.PumpAsync(20);

            var view = window.ChatView;
            var built = view.TranscriptItems.GetRealizedContainers().Count();
            var empty = await AssistantChatScrolling.UpAndDownAsync(view, 120);

            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "go") { ProjectIds = ["p1"] });
            for (var i = 0; i < 60; i++)
            {
                session.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = $"streamed paragraph {i}, wrapping over lines.\n\n" });
                empty += await AssistantChatScrolling.WheelAsync(view, i % 20 < 10 ? -120 : 120);
            }

            session.ShownThread = "p1";
            await AssistantChatScrolling.PumpAsync(20);
            empty += await AssistantChatScrolling.UpAndDownAsync(view, 60);
            window.Close();

            Assert.True(built is > 0 and < 40, $"{built} of 160 rows built: the panel is building history nobody is looking at");
            Assert.True(empty == 0, $"{empty} painted frames had no row on screen while the operator scrolled");
        });
    }
}
