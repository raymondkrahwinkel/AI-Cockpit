using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

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
    public void RecyclingATranscriptRow_DoesNotRebuildTheMarkdownOfTheRowBeingDiscarded() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < 400; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.AssistantText, $"row {index} — a short reply with `code` in it."));
        }

        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);

        var window = new AssistantChatWindow
        {
            Width = 900,
            Height = 900,
            DataContext = new AssistantChatViewModel(
                host,
                Substitute.For<IAssistantSettingsStore>(),
                Substitute.For<IVoicePlaybackQueue>()),
        };

        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var scroll = window.ChatView.TranscriptItems.GetVisualDescendants().OfType<ScrollViewer>().First();
        var watched = window.ChatView.TranscriptItems.GetVisualDescendants().OfType<MarkdownView>()
            .Select(view => (View: view, Renders: view.DebugRenderCount)).ToList();

        // Back and forth rather than one sweep: a row is only discarded once the panel needs its container again.
        for (var step = 0; step < 60; step++)
        {
            scroll.Offset = scroll.Offset.WithY(step % 2 == 0 ? step * 220 : 0);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        var discarded = watched.Where(entry => !entry.View.IsAttachedToVisualTree()).ToList();
        var rebuilds = discarded.Sum(entry => entry.View.DebugRenderCount - entry.Renders);
        window.Close();

        Assert.True(discarded.Count > 0, "no row was recycled away, so nothing was measured");
        Assert.Equal(0, rebuilds);
    });

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

    [Fact]
    public void AViewThatWasNeverAttached_StillRendersOnce() => HeadlessAvalonia.Run(() =>
    {
        // A plugin is handed a drawn control without the view ever reaching a visual tree (MarkdownView.cs:138).
        var view = new MarkdownView { Markdown = "## Heading\n\nA paragraph with `code`.\n" };

        Assert.Equal(1, view.DebugRenderCount);
    });
}
