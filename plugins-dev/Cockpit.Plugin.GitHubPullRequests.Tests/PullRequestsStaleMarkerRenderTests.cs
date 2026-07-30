using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// IL#9 for AC-515 criterion 6: the "old" marker has to be visible but not screaming, and the list must not
/// visibly jump when a stale snapshot is replaced by a fresh one. Both are rendering questions a
/// <see cref="PullRequestRefreshSourceTests"/>-style unit test cannot answer — this drives the real
/// <see cref="GitHubPullRequestsSideSectionControl"/> in a headless (but Skia-measured) Avalonia window.
/// </summary>
[Collection("avalonia")]
public class PullRequestsStaleMarkerRenderTests
{
    private static readonly GitHubPullRequest First = new(41, "Faster startup", "https://github.com/octocat/hello-world/pull/41", "Cold start takes 4s.", "octocat/hello-world", "octocat", DateTimeOffset.UtcNow);
    private static readonly GitHubPullRequest Second = new(42, "Fix the sidebar", "https://github.com/octocat/hello-world/pull/42", "It collapses.", "octocat/hello-world", "octocat", DateTimeOffset.UtcNow);

    [Fact]
    public void AnOldSnapshot_ShowsTheMarker_FaintRatherThanTheReviewRequestedAmber() => HeadlessAvalonia.Run(() =>
    {
        var harness = _Harness.OpenWithOldSnapshot();

        var stale = harness.StaleMarker();
        var isVisible = stale.IsEffectivelyVisible;
        var staleColour = ((SolidColorBrush?)stale.Foreground)?.Color;
        var faintColour = _ResolveColour("CockpitTextFaintBrush");
        var waitingColour = _ResolveColour("CockpitStatusWaitingBrush");

        harness.Close();

        Assert.True(isVisible, "the operator has to be told the list on screen predates this session");
        Assert.Equal(faintColour, staleColour);
        Assert.NotEqual(waitingColour, staleColour);
    });

    [Fact]
    public void AFreshAnswer_ReplacesTheMarker_WithoutMovingAnyRow() => HeadlessAvalonia.Run(() =>
    {
        var harness = _Harness.OpenWithOldSnapshot();

        var rows = harness.Rows();
        var boundsBeforeRefresh = rows.Bounds;
        var childCountBeforeRefresh = rows.Children.Count;
        var staleBeforeRefresh = harness.StaleMarker().IsEffectivelyVisible;

        // The fresh answer carries the same two pull requests — nothing about what is shown should change size,
        // only the marker should go away.
        harness.CompleteRefreshWith(First, Second);

        var boundsAfterRefresh = rows.Bounds;
        var childCountAfterRefresh = rows.Children.Count;
        var staleAfterRefresh = harness.StaleMarker().IsEffectivelyVisible;

        harness.Close();

        Assert.True(staleBeforeRefresh, "the precondition — otherwise this proves nothing about a marker disappearing");
        Assert.False(staleAfterRefresh, "a fresh answer has to clear the marker");
        Assert.Equal(childCountBeforeRefresh, childCountAfterRefresh);
        Assert.Equal(boundsBeforeRefresh, boundsAfterRefresh);
    });

    private static Color _ResolveColour(string brushKey)
    {
        var brush = Application.Current?.TryFindResource(brushKey, out var value) == true ? value as ISolidColorBrush : null;
        return brush?.Color ?? throw new InvalidOperationException($"The theme has no solid-colour brush named '{brushKey}'.");
    }

    /// <summary>One side section under test, in a window its real size, built from an old (pre-restart) persisted snapshot.</summary>
    private sealed class _Harness
    {
        private readonly Window _window;
        private readonly TaskCompletionSource<PullRequestFeedResult> _release;

        private _Harness(Window window, TaskCompletionSource<PullRequestFeedResult> release)
        {
            _window = window;
            _release = release;
        }

        public static _Harness OpenWithOldSnapshot()
        {
            var storage = new InMemoryPluginStorage();
            var oldSnapshot = new PullRequestFeedSnapshot(
                new PullRequestFeedResult([First, Second], [], RepositoryMissing: false),
                DateTimeOffset.UtcNow - PullRequestRefreshSource.StaleAfter - TimeSpan.FromMinutes(1));
            storage.Set("refreshSourceSnapshot", oldSnapshot);

            // A pollInterval long enough that no second tick lands mid-test; the load function hangs on `release`
            // until the test decides what the "fresh" answer is, so the old snapshot stays the only thing on
            // screen until then.
            var release = new TaskCompletionSource<PullRequestFeedResult>();
            var source = new PullRequestRefreshSource(storage, (_, _) => release.Task, TimeSpan.FromMinutes(10));

            var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };
            var control = new GitHubPullRequestsSideSectionControl(settings, new FakeCockpitHost(), source);

            var window = new Window { Width = 420, Height = 600, Content = control };
            window.Show();
            window.UpdateLayout();

            return new _Harness(window, release);
        }

        public TextBlock StaleMarker() => _window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "stale");

        public StackPanel Rows() => _window.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Name == "rows");

        public void CompleteRefreshWith(params GitHubPullRequest[] pullRequests)
        {
            _release.SetResult(new PullRequestFeedResult(pullRequests, [], RepositoryMissing: false));

            // The source's Updated handler marshals through Dispatcher.UIThread.Post (it can also fire from a
            // background timer tick), so the posted re-render needs the queue pumped before this test can see it —
            // same pattern tests elsewhere in this repo use (CanvasRendersTemplateTests, the host's own ViewTests).
            Dispatcher.UIThread.RunJobs();
            _window.UpdateLayout();
        }

        public void Close() => _window.Close();
    }
}
