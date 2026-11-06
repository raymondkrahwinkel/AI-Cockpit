using Cockpit.App.Services;

namespace Cockpit.App.ViewTests.Services;

// The memoised probe behind a clickable code-span (AC-642): never touches disk on the calling thread, and never
// probes twice for what it already knows or is already checking. FilePathResolver.Exists is swapped per test so
// none of this touches the real filesystem; each test uses its own candidate string so the shared static cache
// from an earlier test cannot answer for it.
[Collection("avalonia")]
public sealed class FilePathResolverTests : IDisposable
{
    private readonly Func<string, bool> _originalExists = FilePathResolver.Exists;

    public void Dispose() => FilePathResolver.Exists = _originalExists;

    [Fact]
    public async Task RepaintsWhileAProbeIsInFlight_ShareTheOneProbeAndBothGetNotified()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var probes = 0;
            FilePathResolver.Exists = _ => { Interlocked.Increment(ref probes); return true; };
            var firstSettled = new TaskCompletionSource();
            var secondSettled = new TaskCompletionSource();

            // Same repaint burst a streaming reply produces, but from two different callers (two MarkdownView
            // instances with the same BasePath) — both must still hear the answer once the one shared probe lands.
            FilePathResolver.Resolve("Streaming.cs", @"C:\repo", () => firstSettled.TrySetResult());
            FilePathResolver.Resolve("Streaming.cs", @"C:\repo", () => secondSettled.TrySetResult());

            await Task.WhenAll(
                Task.WhenAny(firstSettled.Task, Task.Delay(TimeSpan.FromSeconds(5))),
                Task.WhenAny(secondSettled.Task, Task.Delay(TimeSpan.FromSeconds(5))));

            Assert.True(firstSettled.Task.IsCompletedSuccessfully);
            Assert.True(secondSettled.Task.IsCompletedSuccessfully);
            Assert.Equal(1, probes);
        });
    }
}
