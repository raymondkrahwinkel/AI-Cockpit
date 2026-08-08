using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What a message may carry in the assistant's chat window (AC-630): an image with no words, and the queued
/// message Arrow-Up pulls back to edit.
/// </summary>
/// <remarks>
/// Here rather than in the unit tests because queuing an attachment decodes a preview bitmap, which Avalonia
/// cannot do without a platform.
/// </remarks>
[Collection("avalonia")]
public class AssistantChatComposerTests
{
    /// <summary>A real 1×1 PNG — the attachment chip decodes what it is given.</summary>
    private static byte[] Png => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// Criterion 4: a pasted image with nothing typed goes, instead of hanging in the strip forever. Both gates
    /// used to refuse it — the window's CanSend and the host's own empty-text guard.
    /// </summary>
    [Fact]
    public async Task AMessageWithOnlyAnImage_Sends() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var session = new SessionViewModel();
        var host = _FakeHost(session);
        var vm = new AssistantChatViewModel(host, _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());
        session.PendingAttachments.Add(new ImageAttachmentViewModel(Png, a => session.PendingAttachments.Remove(a)));

        Assert.True(vm.SendCommand.CanExecute(null));

        await vm.SendCommand.ExecuteAsync(null);

        await host.Received().SendAsync(string.Empty, Arg.Any<CancellationToken>());
    });

    /// <summary>
    /// Criterion 5: Arrow-Up on an empty box brings the last queued message back to edit. The session's own recall
    /// puts it in the session's composer, which this window does not show — so it has to land in the window's box.
    /// </summary>
    [Fact]
    public void RecallingAQueuedMessage_PutsItInTheWindowsOwnInputBox() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        // The parameterless constructor is the previewer's — it seeds a sample queued message; a real session
        // starts with an empty queue.
        session.QueuedMessages.Clear();
        var vm = new AssistantChatViewModel(_FakeHost(session), _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());
        session.QueuedMessages.Add(new QueuedMessageViewModel("kijk hier nog eens naar", [], m => session.QueuedMessages.Remove(m)));

        Assert.True(vm.RecallLastQueuedMessage());

        Assert.Equal("kijk hier nog eens naar", vm.InputText);
        Assert.Empty(session.InputText);
        Assert.Empty(session.QueuedMessages);
    });

    /// <summary>An empty queue leaves Arrow-Up free to move the caret as usual.</summary>
    [Fact]
    public void RecallingWithAnEmptyQueue_DoesNothing() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.QueuedMessages.Clear();
        var vm = new AssistantChatViewModel(_FakeHost(session), _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        Assert.False(vm.RecallLastQueuedMessage());
        Assert.Empty(vm.InputText);
    });

    private static IAssistantSessionHost _FakeHost(SessionViewModel session)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        host.Activity.Returns(AssistantActivity.Ready);
        host.EnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<SessionViewModel?>(session));
        host.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return host;
    }

    private static IAssistantSettingsStore _FakeSettingsStore()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        return store;
    }
}
