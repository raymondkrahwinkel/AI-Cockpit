using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The two halves of the streaming runaway that took a machine to ~25 GB: a markdown view that rebuilt its whole
/// block tree on every delta, and a pane-visibility style broad enough to bind IsPaneVisible against every
/// transcript row underneath it. Both only bite while an SDK session streams, which is why they went unseen.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptStreamingCostTests
{
    [Fact]
    public async Task ABurstOfStreamingDeltas_RepaintsFarFewerTimesThanItHasDeltas()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var view = new MarkdownView();
            var window = new Window { Content = view };
            window.Show();

            var rendered = Assert.IsType<StackPanel>(view.Content);

            // A reply arriving the way the SDK sends one: many small appends, back to back. The loop holds the
            // dispatcher, so the timer cannot tick inside it — exactly the burst the rate limit exists for.
            var text = string.Empty;
            for (var i = 0; i < 200; i++)
            {
                text += $"chunk {i} of a reply that keeps growing.\n\n";
                view.Markdown = text;
            }

            // Only the leading-edge repaint has run; the other 199 deltas were coalesced. Were each one painting,
            // every paragraph would already be on screen.
            Assert.True(
                rendered.Children.Count < 200,
                $"a 200-delta burst painted {rendered.Children.Count} paragraphs; the rate limit is not holding");

            // The last delta must still land: the tick after the burst flushes it. The burst left a rebuild
            // pending on the timer's next tick, so wait for that real completion rather than guessing a delay
            // that outlasts it.
            await view.WaitForPendingRenderAsync();
            Assert.Equal(200, rendered.Children.Count);
        });
    }

    /// <summary>
    /// The rate limit caps how often a repaint happens; this caps how much one costs. A delta only ever touches
    /// the block at the end, so every block before it must keep the controls it already had — otherwise a repaint
    /// is O(reply length) and a long answer stays quadratic overall, merely at 30 fps rather than per delta.
    /// </summary>
    [Fact]
    public async Task AppendingToAReply_LeavesTheBlocksBeforeItUntouched()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var view = new MarkdownView();
            var window = new Window { Content = view };
            window.Show();

            var rendered = Assert.IsType<StackPanel>(view.Content);

            // The first set on a fresh view renders inline (no rebuild timer running yet to coalesce into).
            var text = "first paragraph, written once.\n\nsecond paragraph.\n\n";
            view.Markdown = text;

            Assert.Equal(2, rendered.Children.Count);
            var first = rendered.Children[0];
            var second = rendered.Children[1];

            for (var i = 0; i < 20; i++)
            {
                text += $"later paragraph {i}.\n\n";
                view.Markdown = text;

                // The first render started the rebuild timer, so every set after it only flags a pending rebuild
                // and returns; wait for the real tick that applies it instead of guessing it lands within 50ms.
                await view.WaitForPendingRenderAsync();
            }

            Assert.Equal(22, rendered.Children.Count);
            Assert.Same(first, rendered.Children[0]);
            Assert.Same(second, rendered.Children[1]);
        });
    }

    /// <summary>
    /// The other direction: markdown that shrinks, which is what a recycled transcript row hands over when it is
    /// reused for a shorter message. The leftover tail has to go rather than linger under the new text.
    /// </summary>
    [Fact]
    public async Task MarkdownThatShrinks_DropsTheBlocksThatAreGone()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var view = new MarkdownView();
            var window = new Window { Content = view };
            window.Show();

            var rendered = Assert.IsType<StackPanel>(view.Content);

            // First set on a fresh view renders inline — no rebuild timer running yet to coalesce into.
            view.Markdown = "one.\n\ntwo.\n\nthree.\n\nfour.\n\n";
            Assert.Equal(4, rendered.Children.Count);

            // This set lands while the timer from the first render is still running, so it only flags a pending
            // rebuild; wait for the tick that actually applies it.
            view.Markdown = "just the one now.\n\n";
            await view.WaitForPendingRenderAsync();
            Assert.Single(rendered.Children);
        });
    }

    /// <summary>
    /// Guards the selector itself rather than a realised tree: reparenting a live style to exercise it throws, and
    /// a failed binding leaves IsVisible at its default true, so a matched presenter looks identical to an
    /// unmatched one from the outside. What actually went wrong is that a selector had no parent constraint, so
    /// that is what is pinned here — read off the shipped view, not restated.
    ///
    /// A descendant selector rooted on the long-lived grid does more than mis-match: its StyleClassActivator
    /// installs an IClassesChangedListener on the recycled rows it reaches and never tears it off, pinning every
    /// row a streaming pane ever built (measured ~600 rows / 360MB). So each grid style must reach panes WITHOUT a
    /// descendant hop into the pane subtree — two safe shapes: the pane-visibility style keeps its `>` child
    /// constraint to the grid's own containers; the rail miniature styles (AC-670) key off the pane element's OWN
    /// inherited SessionTilePanel.IsMiniature (a self selector), which is not a descendant reach at all.
    /// </summary>
    [Fact]
    public void TheSessionGridsPaneVisibilityStyle_IsScopedToPanesAndCannotReachTranscriptRows()
    {
        HeadlessAvalonia.Run(() =>
        {
            var view = new CockpitView();
            var sessionGrid = view.GetLogicalDescendants()
                .OfType<ItemsControl>()
                .First(c => c.Name == "SessionGrid");

            Assert.NotEmpty(sessionGrid.Styles);
            foreach (var style in sessionGrid.Styles)
            {
                var selector = Assert.IsType<Style>(style).Selector;
                Assert.NotNull(selector);

                var text = selector!.ToString()!;

                // Safe shape 1: scoped by the child combinator to the grid's own generated containers, so it cannot
                // match a #PART_ContentPresenter deeper in a session view whose DataContext is a transcript entry.
                var childScopedToGrid = text.Contains("SessionGrid", StringComparison.Ordinal)
                    && text.Contains(">", StringComparison.Ordinal)
                    && text.Contains("ContentPresenter", StringComparison.Ordinal);

                // Safe shape 2: a self selector keyed on the element's own inherited SessionTilePanel.IsMiniature —
                // no descendant hop, so no listener is installed on (and no row is pinned by) the recycled subtree.
                var selfKeyedOnIsMiniature = text.Contains("IsMiniature", StringComparison.Ordinal)
                    && !text.Contains(">", StringComparison.Ordinal);

                Assert.True(
                    childScopedToGrid || selfKeyedOnIsMiniature,
                    $"A SessionGrid style can reach (and pin) transcript rows: {text}");
            }
        });
    }

    /// <summary>
    /// The other half of scoping that selector: it still has to do its job. Narrowing it to the panel's own
    /// children would silently stop hiding panes — single-pane and Zoom layouts would show every session at
    /// once — and no existing test realises the grid to notice.
    /// </summary>
    [Fact]
    public void APaneWithIsPaneVisibleFalse_IsStillCollapsedByThatStyle()
    {
        HeadlessAvalonia.Run(() =>
        {
            var view = new CockpitView();
            var sessionGrid = view.GetLogicalDescendants()
                .OfType<ItemsControl>()
                .First(c => c.Name == "SessionGrid");

            var shown = new PaneStub { IsPaneVisible = true };
            var hidden = new PaneStub { IsPaneVisible = false };
            sessionGrid.ItemsSource = new[] { shown, hidden };

            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            window.UpdateLayout();

            var containers = sessionGrid.GetVisualDescendants()
                .OfType<SessionTilePanel>()
                .Single()
                .Children
                .OfType<ContentPresenter>()
                .ToList();

            Assert.Equal(2, containers.Count);
            Assert.True(containers[0].IsVisible, "a visible pane must stay on screen");
            Assert.False(containers[1].IsVisible, "IsPaneVisible=false must still collapse the pane's container");
        });
    }

    /// <summary>
    /// How many times a streamed reply is applied, which is what decides its cost: the row realises its whole text
    /// per call and the binding reads it back on every change, so the split is the bill (AC-529).
    /// </summary>
    [Fact]
    public async Task AReplyStreamedAtTheRateOneArrives_IsAppliedFarFewerTimesThanItHasDeltas()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            const int deltas = 400;
            const int deltaChars = 40;

            var applies = 0;
            var text = new System.Text.StringBuilder();
            var queue = new SessionEventQueue(evt =>
            {
                applies++;
                text.Append(((AssistantTextDelta)evt).Text);
            });

            var chunk = new string('x', deltaChars);

            // Paced, not flooded, and that pacing is the test: a tight producer outruns the dispatcher and batches
            // even without a window, which is how the self-clocking version passed a test while doing nothing on a
            // live session (AC-529).
            var producing = System.Diagnostics.Stopwatch.StartNew();

            await Task.Run(() =>
            {
                for (var i = 0; i < deltas; i++)
                {
                    queue.Enqueue(new AssistantTextDelta { Text = chunk, SessionId = "s", BlockIndex = 0 });
                    Thread.Sleep(1);
                }
            });

            producing.Stop();

            // The test measures how the windowed drain folded deltas over real time, so it must let that drain
            // run its own course rather than lump everything into one apply — wait for the queue to report itself
            // idle (HasWork false), then flush whatever a still-open window left behind.
            await _WaitUntilAsync(() => !queue.HasWork);
            queue.Flush();

            // Nothing may be lost, however the folding falls out.
            Assert.Equal(deltas * deltaChars, text.Length);

            // Measured against how long the producer actually took, not against a fraction of the delta count: the
            // window folds per unit of time, so a fixed fraction is really an assertion about the host's timer
            // resolution. Where Thread.Sleep(1) rounds up to ~15ms this run stretches to six seconds and a fifth of
            // 400 is simply the wrong line — a red test about the machine rather than about the code. One apply per
            // window is the ceiling the design promises; the slack covers the leading edge, the flush and a window
            // that lands either side of the boundary.
            const int windowMs = 33;
            var ceiling = (int)(producing.ElapsedMilliseconds / windowMs) + 4;

            Assert.True(
                applies <= ceiling,
                $"{deltas} deltas produced over {producing.ElapsedMilliseconds} ms were applied {applies} times, "
                + $"more than the {ceiling} a {windowMs} ms window allows; they are not being folded, so each one "
                + "still realises the whole reply");

            // And folding has to be doing something at all, whatever the clock did.
            Assert.True(applies < deltas, $"every one of {deltas} deltas was applied on its own");
        });
    }

    /// <summary>
    /// The price of the drain window: a pane that stops listening mid-window would drop what the queue was holding,
    /// which is the property the self-clocking version got for free (AC-529).
    /// </summary>
    [Fact]
    public void FlushingTheQueue_AppliesWhatTheDrainWindowWasStillHolding()
    {
        var applied = new List<string>();

        // A pump that never runs anything — the state a pane detaching mid-window is in.
        var queue = new SessionEventQueue(
            evt => applied.Add(((AssistantTextDelta)evt).Text),
            _ => { });

        queue.Enqueue(new AssistantTextDelta { Text = "one", SessionId = "s", BlockIndex = 0 });
        queue.Enqueue(new AssistantTextDelta { Text = "two", SessionId = "s", BlockIndex = 1 });

        Assert.Empty(applied);

        queue.Flush();

        Assert.Equal(["one", "two"], applied);
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition should become true within the poll window");
    }

    // The template binds by reflection, so the grid only needs an object carrying IsPaneVisible; a real
    // SessionPanelViewModel would drag a session process's worth of dependencies in for nothing.
    private sealed class PaneStub
    {
        public bool IsPaneVisible { get; init; }
    }
}
