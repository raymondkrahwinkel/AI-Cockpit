#if DEBUG
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Cockpit.App.Diagnostics;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-878: applies TranscriptLeakHuntTests' hunt (cc85ca1e) to AssistantChatView, structurally closest to
// SessionView. Stays green with no fix — rows do realise before detach, so this is a real finding, not a
// vacuous test. See CompositorTeardown.Flush's caller in SessionView for the leak this rules out here.
[Collection("avalonia")]
public sealed class AssistantChatLeakHuntTests
{
    [Fact]
    public async Task DetachingTheViewWithoutARender_AlreadyReleasesIt()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            LeakTracker.Reset();      // count only this test's AssistantChatView, not leftovers from other tests
            await _BuildDetachNoRenderAsync();   // realise + detach, no render pass of ours

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var beforePump = LeakTracker.AliveCount(nameof(AssistantChatView));

            // No explicit commit, no UpdateLayout of our own — same pump TranscriptLeakHuntTests uses.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(120);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var afterPump = LeakTracker.AliveCount(nameof(AssistantChatView));

            Assert.True(
                afterPump == 0,
                $"paused-detach AssistantChatView alive: right after detach={beforePump}, after the pump={afterPump} "
                + "(expected 0 with no fix — regression guard: if this ever goes non-zero, AssistantChatView has "
                + "started carrying the AC-878 leak and needs CompositorTeardown.Flush like SessionView)");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task _BuildDetachNoRenderAsync()
    {
        var session = new SessionViewModel();
        for (var i = 0; i < 60; i++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {i}"));
        }

        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);

        var vm = new AssistantChatViewModel(
            host,
            Substitute.For<IAssistantSettingsStore>(),
            Substitute.For<IVoicePlaybackQueue>());

        // Plain Window, not AssistantChatWindow: that class's generated `ChatView` field would keep the view
        // alive regardless of Content, testing field retention instead of the compositor.
        var view = new AssistantChatView { DataContext = vm };
        var window = new Window { Content = view, Width = 420, Height = 560 };
        window.Show();

        // A few passes: EnsureOpenedAsync and the transcript attach both settle async, and each row's body
        // materialises lazily, so one pass alone realises nothing to actually orphan.
        for (var warmup = 0; warmup < 4; warmup++)
        {
            window.UpdateLayout();
            await Task.Delay(40);
        }

        // Detach WITHOUT a following render pass, host window left open — the AC-953 dock/undock shape.
        window.Content = new Border();
        view.DataContext = null;
    }
}
#endif
