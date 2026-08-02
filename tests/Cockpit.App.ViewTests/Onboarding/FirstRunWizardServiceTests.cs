using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// <see cref="FirstRunWizardService"/>'s own two edges: a cancelled token must not leave the caller waiting
/// forever (coordinator finding #4), and the window must be owned by the main window rather than shown bare
/// (coordinator finding #2 — AC-543's own defect #9: an ownerless window outlives the cockpit and keeps the
/// process, and its global hotkeys, alive after the operator thinks it is gone).
/// </summary>
[Collection("avalonia")]
public class FirstRunWizardServiceTests
{
    [Fact]
    public async Task ShowAsync_TokenAlreadyCancelled_ThrowsInsteadOfHanging_AndNeverMarksComplete()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var stateStore = new RecordingStateStore();
            var service = new FirstRunWizardService(
                [new StubFirstRunWizardStep(0, "What this is", isSkipped: false)], stateStore);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Bounded, so a regression that brings back the hang fails this test instead of the whole run.
            var showing = service.ShowAsync(cts.Token);
            var finished = await Task.WhenAny(showing, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(showing, finished);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => showing);
            Assert.Null(stateStore.MarkedVersion);
        });
    }

    /// <summary>
    /// Read off the source rather than run: there is no <c>IClassicDesktopStyleApplicationLifetime</c> in the
    /// headless harness (the same gap <c>DialogModalitySplitTests</c> notes for <c>SessionDialogService</c>), so
    /// the owned branch of <c>ShowAsync</c> can never actually execute here.
    /// </summary>
    [Fact]
    public void ShowAsync_OwnsTheWindowByTheMainWindow_WhenOneExists()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "src", "Cockpit.App", "Services", "FirstRunWizardService.cs"));

        Assert.Contains(
            "IClassicDesktopStyleApplicationLifetime { MainWindow: { } main }", source, StringComparison.Ordinal);
        Assert.Contains("window.Show(main)", source, StringComparison.Ordinal);
    }

    private sealed class RecordingStateStore : IFirstRunWizardStateStore
    {
        public int? MarkedVersion { get; private set; }

        public Task<int?> GetCompletedVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public Task MarkCompletedAsync(int version, CancellationToken cancellationToken = default)
        {
            MarkedVersion = version;

            return Task.CompletedTask;
        }
    }
}
