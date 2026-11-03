using Cockpit.App.Services;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// <see cref="FirstRunWizardService"/>: a cancelled token must not leave the caller waiting forever
/// (coordinator finding #4).
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
