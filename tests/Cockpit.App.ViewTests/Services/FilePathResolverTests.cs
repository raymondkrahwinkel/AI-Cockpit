using Cockpit.App.Services;

namespace Cockpit.App.ViewTests.Services;

/// <summary>
/// The memoised probe behind a clickable code-span (AC-642): never touches disk on the calling thread, and never
/// probes twice for what it already knows or is already checking. <see cref="FilePathResolver.Exists"/> is swapped
/// per test so none of this touches the real filesystem; each test uses its own candidate string so the shared
/// static cache from an earlier test cannot answer for it.
/// </summary>
[Collection("avalonia")]
public sealed class FilePathResolverTests : IDisposable
{
    private readonly Func<string, bool> _originalExists = FilePathResolver.Exists;

    public void Dispose() => FilePathResolver.Exists = _originalExists;

    [Fact]
    public async Task UnknownCandidate_ReturnsNullThenSettlesToTheFullPath()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            FilePathResolver.Exists = _ => true;
            var settled = new TaskCompletionSource();

            var immediate = FilePathResolver.Resolve("Theme.axaml", @"C:\repo", () => settled.TrySetResult());
            Assert.Null(immediate);

            await Task.WhenAny(settled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(settled.Task.IsCompletedSuccessfully);

            var afterSettle = FilePathResolver.Resolve("Theme.axaml", @"C:\repo", () => { });
            Assert.Equal(Path.Combine(@"C:\repo", "Theme.axaml"), afterSettle);
        });
    }

    [Fact]
    public async Task MissingFile_SettlesToNullAndStaysNegative()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var probes = 0;
            FilePathResolver.Exists = _ => { Interlocked.Increment(ref probes); return false; };
            var settled = new TaskCompletionSource();

            FilePathResolver.Resolve("Ghost.cs", @"C:\repo", () => settled.TrySetResult());
            await Task.WhenAny(settled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(settled.Task.IsCompletedSuccessfully);

            Assert.Null(FilePathResolver.Resolve("Ghost.cs", @"C:\repo", () => { }));
            Assert.Equal(1, probes); // the second call answered from the (negative) cache entry
        });
    }

    [Fact]
    public void NoBasePathAndRelativeCandidate_ReturnsNullWithoutProbing()
    {
        var probed = false;
        FilePathResolver.Exists = _ => { probed = true; return true; };

        var result = FilePathResolver.Resolve("Theme.axaml", null, () => { });

        Assert.Null(result);
        Assert.False(probed);
    }

    [Fact]
    public async Task CachedPositive_IsNeverProbedTwice()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var probes = 0;
            FilePathResolver.Exists = _ => { Interlocked.Increment(ref probes); return true; };
            var settled = new TaskCompletionSource();

            FilePathResolver.Resolve("MarkdownView.cs", @"C:\repo\src", () => settled.TrySetResult());
            await Task.WhenAny(settled.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            FilePathResolver.Resolve("MarkdownView.cs", @"C:\repo\src", () => { });
            FilePathResolver.Resolve("MarkdownView.cs", @"C:\repo\src", () => { });

            Assert.Equal(1, probes);
        });
    }

    [Fact]
    public async Task RepaintsWhileAProbeIsInFlight_ShareTheOneProbe()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var probes = 0;
            FilePathResolver.Exists = _ => { Interlocked.Increment(ref probes); return true; };
            var firstSettled = new TaskCompletionSource();

            // Same repaint burst a streaming reply produces: several calls for the same candidate before the
            // first probe has answered. Only the first caller's callback is the one that fires.
            FilePathResolver.Resolve("Streaming.cs", @"C:\repo", () => firstSettled.TrySetResult());
            FilePathResolver.Resolve("Streaming.cs", @"C:\repo", () => throw new InvalidOperationException(
                "a second caller must never get its own probe while one is already pending"));

            await Task.WhenAny(firstSettled.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.True(firstSettled.Task.IsCompletedSuccessfully);
            Assert.Equal(1, probes);
        });
    }
}
