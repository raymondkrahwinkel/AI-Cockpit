using Avalonia.Controls;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The two halves of the streaming runaway that took a machine to ~25 GB: a markdown view that rebuilt its whole
/// block tree on every delta, and a pane-visibility style broad enough to bind IsPaneVisible against every
/// transcript row underneath it. Both only bite while an SDK session streams, which is why they went unseen.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptStreamingCostTests
{
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
            _ => { },
            _ => { });

        queue.Enqueue(new AssistantTextDelta { Text = "one", SessionId = "s", BlockIndex = 0 });
        queue.Enqueue(new AssistantTextDelta { Text = "two", SessionId = "s", BlockIndex = 1 });

        Assert.Empty(applied);

        queue.Flush();

        Assert.Equal(["one", "two"], applied);
    }
}
