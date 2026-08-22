#if DEBUG
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Diagnostics;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// Hunts the production leak with the REAL SessionView + real transcript rows + real MarkdownView, headless.
// LeakTracker (DEBUG) counts how many row controls survive a forced GC after we scroll past hundreds of them.
// A correctly virtualising + releasing transcript keeps only a viewport's worth alive; the production dump found
// thousands, so if this fails the leak lives in the real SessionView/row/MarkdownView combination.
[Collection("avalonia")]
public sealed class TranscriptLeakHuntTests
{
    private static string MarkdownDoc(int i) =>
        $"## Heading {i}\n\nSome **bold**, some `inline code`, and a [link](https://example.com/{i}).\n\n"
        + $"```csharp\nvar value{i} = Compute(\"{i}\");\n```\n\n- first item {i}\n- second item {i}\n\n"
        + $"A closing paragraph for message {i} with a path `src/App/File{i}.cs` in it.\n";

    [Fact]
    public async Task ScrollingPastHundredsOfRealTranscriptRows_ReleasesThem()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            LeakTracker.Reset();
            var vm = new SessionViewModel();
            // AC-990: through Transcript, the way the app fills it — that is what stamps each row's session and
            // syncs it into the VisibleTranscript the ItemsControl binds to.
            for (var i = 0; i < 400; i++)
            {
                vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, MarkdownDoc(i)));
            }

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 820, Height = 640 };
            window.Show();
            // A few layout passes, not one: each row's body is built lazily (a ContentControl whose implicit
            // DataTemplate only materialises once its branch content resolves), so the first pass measures short
            // rows and the panel needs the follow-up passes to realise a viewport's worth. In the live app the
            // continuous render loop does this within a frame or two; here we pump it explicitly.
            for (var warmup = 0; warmup < 4; warmup++)
            {
                window.UpdateLayout();
                await Task.Delay(40);
            }

            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "TranscriptScroll");
            Assert.NotNull(scroll);

            var realizedAtTop = LeakTracker.AliveCount(nameof(TranscriptRowView));
            var mdAtTop = LeakTracker.AliveCount(nameof(MarkdownView));
            Assert.True(realizedAtTop > 2, $"no rows realised (rows={realizedAtTop} md={mdAtTop}); the test is not exercising the transcript");

            // Scroll top -> bottom -> top a few times, so rows dematerialise and re-realise repeatedly (the churn the
            // production dump showed: more rows created than there are view-models).
            for (var pass = 0; pass < 3; pass++)
            {
                for (var step = 0; step <= 20; step++)
                {
                    scroll!.Offset = scroll.Offset.WithY(Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height) * step / 20);
                    window.UpdateLayout();
                    await Task.Delay(10);
                }
            }

            var aliveRows = LeakTracker.AliveCount(nameof(TranscriptRowView));
            var aliveMd = LeakTracker.AliveCount(nameof(MarkdownView));

            // 400 rows, only a viewport (~dozen) visible: virtualisation + teardown should keep the survivors far
            // below the full set. If these are near 400+, the real combination is leaking dematerialised rows.
            // Tight, to reveal the actual survivor count in the failure message (viewport is ~a dozen rows).
            // Tight, to reveal the actual survivor count in the failure message (viewport is ~a dozen rows).
            Assert.True(aliveRows < 20, $"ACTUAL after scroll: TranscriptRowView={aliveRows} MarkdownView={aliveMd} (of 400)");
            Assert.True(aliveMd < 40, $"ACTUAL after scroll: TranscriptRowView={aliveRows} MarkdownView={aliveMd} (of 400)");
        });
    }

    // The production scenario: panes are opened and closed over and over. If each close leaves its SessionView +
    // rows behind, the count grows with the number of closes — the permanent leak. If it stays ~1, the last one is
    // just waiting on a deferred teardown (transient).
    [Fact]
    public async Task RepeatedlyClosingRealSessionPanes_DoesNotAccumulateThem()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = new Window { Width = 820, Height = 640 };
            window.Show();
            LeakTracker.Reset();

            const int closes = 20;
            for (var pane = 0; pane < closes; pane++)
            {
                await _BuildRealiseAndDetach(window);
            }

            for (var i = 0; i < 6; i++)
            {
                window.UpdateLayout();
                await Task.Delay(30);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            var views = LeakTracker.AliveCount(nameof(SessionView));
            var rows = LeakTracker.AliveCount(nameof(TranscriptRowView));
            var md = LeakTracker.AliveCount(nameof(MarkdownView));

            // If close released, at most the most-recent pane lingers. If it accumulates, views ~= closes.
            Assert.True(
                views < 3,
                $"after {closes} pane closes: SessionView={views} TranscriptRowView={rows} MarkdownView={md} — panes accumulate on close (LEAK, scales with closes)");
        });
    }

    // The permanent-orphan scenario and its fix: a pane closed while its renderer is paused (background tab/desk)
    // gets no render pass to flush the compositor teardown, so its detached subtree lingers in the scene. The fix
    // is SessionView.OnDetachedFromVisualTree forcing a Compositor.RequestCommitAsync(); this checks that a close
    // with no render of our own — only the dispatcher pumped so the fix's fire-and-forget commit runs — releases it.
    [Fact]
    public async Task ClosingAPaneWithoutARender_IsReleasedByTheOnDetachedCommit()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = new Window { Width = 820, Height = 640 };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(40);

            LeakTracker.Reset();            // count only this test's SessionView, not leftovers from other tests
            _BuildDetachNoRender(window);   // realise + detach, no render pass of ours — the fix schedules the commit

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var beforePump = LeakTracker.AliveCount(nameof(SessionView));

            // Pump the dispatcher so the fix's fire-and-forget RequestCommitAsync runs — no explicit commit and no
            // UpdateLayout of our own, so the SessionView is released purely by what OnDetachedFromVisualTree did.
            // CompositorTeardown.Flush is deliberately fire-and-forget (no task the test could await), so poll for
            // the release instead of guessing how long its commit takes.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var afterPump = beforePump;
            for (var wait = 0; wait < 20 && afterPump != 0; wait++)
            {
                await Task.Delay(20);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                afterPump = LeakTracker.AliveCount(nameof(SessionView));
            }

            Assert.True(
                afterPump == 0,
                $"paused-close SessionView alive: right after close={beforePump}, after the OnDetached commit ran={afterPump} "
                + "(the fix should drive this to 0 without any render of our own)");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _BuildDetachNoRender(Window window)
    {
        var vm = new SessionViewModel();
        for (var i = 0; i < 60; i++)
        {
            vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, MarkdownDoc(i)));
        }

        var view = new SessionView { DataContext = vm };
        window.Content = view;
        window.UpdateLayout();   // realise it

        // Close WITHOUT a following render pass — the background-tab case.
        window.Content = new Border();
        view.DataContext = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task _BuildRealiseAndDetach(Window window)
    {
        var vm = new SessionViewModel();
        for (var i = 0; i < 200; i++)
        {
            vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, MarkdownDoc(i)));
        }

        var view = new SessionView { DataContext = vm };
        window.Content = view;
        window.UpdateLayout();
        await Task.Delay(80);

        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "TranscriptScroll");
        if (scroll is not null)
        {
            scroll.Offset = scroll.Offset.WithY(600);
            window.UpdateLayout();
            await Task.Delay(40);
        }

        // Close the pane: drop it from the tree and clear its data context, exactly what closing a session does.
        window.Content = new Border();
        view.DataContext = null;
        window.UpdateLayout();
        await Task.Delay(40);
        // view + vm fall out of scope here, so only the framework can still be holding them.
    }
}

#endif
