using Avalonia.Controls;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1262: the freeze of 2026-08-31. A virtualising transcript recycles a row inside the layout pass; tearing the
/// row's bindings down wrote <c>Markdown</c> from a source falling away, and the view rebuilt its whole tree for a
/// control being discarded — 104 of the 120 elements that never converged sat under TranscriptRowView. The naive
/// guard (bail whenever not attached) breaks the other half of the contract, so both halves are held here.
/// </summary>
[Collection("avalonia")]
public sealed class Ac1262MarkdownRecycleTests
{
    [Fact]
    public void ARenderDeferredWhileRecycledAway_IsPaidWhenTheViewIsUsedAgain() => HeadlessAvalonia.Run(() =>
    {
        var view = new MarkdownView { Markdown = "## First" };
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        window.UpdateLayout();

        window.Content = null;
        window.UpdateLayout();
        var whileAway = view.DebugRenderCount;

        view.Markdown = "## Second";
        window.Content = view;
        window.UpdateLayout();
        window.Close();

        Assert.Equal(whileAway + 1, view.DebugRenderCount);
    });
}
