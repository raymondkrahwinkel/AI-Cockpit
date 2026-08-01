using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Views;

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
    public async Task ABurstOfStreamingDeltas_RebuildsFarFewerTimesThanItHasDeltas()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var view = new MarkdownView();
            var window = new Window { Content = view };
            window.Show();

            var builds = 0;
            view.PropertyChanged += (_, e) =>
            {
                if (e.Property == ContentControl.ContentProperty)
                {
                    builds++;
                }
            };

            // A reply arriving the way the SDK sends one: many small appends, back to back.
            var text = string.Empty;
            for (var i = 0; i < 200; i++)
            {
                text += $"chunk {i} of a reply that keeps growing.\n\n";
                view.Markdown = text;
            }

            Assert.True(builds < 20, $"a 200-delta burst rebuilt the tree {builds} times; the rate limit is not holding");

            // The last delta must still land: the tick after the burst flushes it.
            await Task.Delay(200);
            var rendered = Assert.IsType<StackPanel>(view.Content);
            Assert.Equal(200, rendered.Children.Count);
        });
    }

    /// <summary>
    /// Guards the selector itself rather than a realised tree: reparenting a live style to exercise it throws, and
    /// a failed binding leaves IsVisible at its default true, so a matched presenter looks identical to an
    /// unmatched one from the outside. What actually went wrong is that the selector had no parent constraint, so
    /// that is what is pinned here — read off the shipped view, not restated.
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

            var selector = Assert.IsType<Style>(sessionGrid.Styles.Single()).Selector;
            Assert.NotNull(selector);

            var text = selector!.ToString()!;
            Assert.Contains("ContentPresenter", text);

            // The child combinator is the whole point: without it the selector also matches every
            // #PART_ContentPresenter deeper in each session view, whose DataContext is a transcript entry.
            Assert.Contains(">", text);
            Assert.Contains("SessionGrid", text);
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

    // The template binds by reflection, so the grid only needs an object carrying IsPaneVisible; a real
    // SessionPanelViewModel would drag a session process's worth of dependencies in for nothing.
    private sealed class PaneStub
    {
        public bool IsPaneVisible { get; init; }
    }
}
