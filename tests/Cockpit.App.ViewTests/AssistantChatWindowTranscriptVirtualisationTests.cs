using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-774: AC-722 shared TranscriptRowView between SessionView and AssistantChatWindow but only wired
/// SessionView's own copy to the viewport-safe VirtualizingStackPanel TranscriptVirtualisationTests guards
/// (AC-686) — this window's copy kept a plain StackPanel, so every row a session ever produced stayed a live
/// control tree forever. Same test as TranscriptVirtualisationTests, aimed at this window's own transcript.
/// </summary>
[Collection("avalonia")]
public class AssistantChatWindowTranscriptVirtualisationTests
{
    [Fact]
    public void ALongTranscript_BuildsOnlyTheRowsThatFitOnScreen() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < 400; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

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
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The panel's own count of what it has built, rather than a sweep of the visual tree — same evidence
        // TranscriptVirtualisationTests uses for SessionView.
        var built = window.ChatView.TranscriptItems.GetRealizedContainers().Count();

        window.Close();

        // A 560px-tall window cannot show four hundred rows; anything close to four hundred means the panel is
        // building the whole history again — exactly the AC-722 regression this test exists to catch.
        Assert.True(built > 0, "the rows on screen must actually be there");
        Assert.True(built < 100, $"{built} of 400 rows built: the panel is building history nobody is looking at");
    });
}
