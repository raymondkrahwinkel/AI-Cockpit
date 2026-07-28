using Cockpit.Core.Updates;
using Cockpit.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// The check itself (AC-387) — the path that had no test at all while the cockpit asked GitHub's releases API by
/// hand, which is how a check could have been reading the wrong feed for a release and nothing would have said so.
/// <para>
/// Driven through a real <c>UpdateManager</c> with the feed and the installation stood in for: a fake
/// <see cref="IUpdateSource"/> records which channel it was asked for, and a <see cref="TestVelopackLocator"/> makes
/// the process look installed. So what is exercised is the manager's own comparison, not a re-implementation of it.
/// </para>
/// </summary>
public class VelopackUpdateServiceTests : IDisposable
{
    private readonly string _packages = Directory.CreateTempSubdirectory("ac387-").FullName;

    public void Dispose() => Directory.Delete(_packages, recursive: true);

    /// <summary>
    /// The name that reaches the feed carries the platform. One GitHub release holds all three platforms' packages,
    /// so a channel named only for the stream is how a Windows install would be offered a macOS package.
    /// </summary>
    [Theory]
    [InlineData(UpdateChannel.Stable, "stable")]
    [InlineData(UpdateChannel.Nightly, "nightly")]
    public async Task TheChannelTheFeedIsAskedFor_NamesThisPlatformAndTheStream(UpdateChannel channel, string stream)
    {
        var feed = new Feed();

        await Check(channel, feed);

        Assert.Equal($"{UpdateChannelName.Platform()}-{stream}", feed.AskedFor);
    }

    /// <summary>A build higher than the installed one is what the operator is told about, with the notes the feed carried.</summary>
    [Fact]
    public async Task ANewerBuildOnTheChannel_IsOffered()
    {
        var result = await Check(UpdateChannel.Stable, new Feed(Package("0.9.0", "what changed")));

        Assert.Null(result.Failure);
        Assert.Equal("0.9.0", result.Release?.Version);
        Assert.Equal("what changed", result.Release?.Notes);
    }

    /// <summary>
    /// The nightly's link is the rolling tag it is published onto; a release's is its own. The feed is a list of
    /// packages and carries neither, so this is the derivation that stands in for one — and it is what the banner's
    /// "Open the release" opens.
    /// </summary>
    [Theory]
    [InlineData(UpdateChannel.Stable, "0.9.0", "https://github.com/raymondkrahwinkel/AI-Cockpit/releases/tag/v0.9.0")]
    [InlineData(UpdateChannel.Nightly, "0.9.0-nightly.7", "https://github.com/raymondkrahwinkel/AI-Cockpit/releases/tag/nightly")]
    public async Task TheLink_PointsAtTheTagTheBuildWasPublishedUnder(UpdateChannel channel, string version, string expected)
    {
        var result = await Check(channel, new Feed(Package(version)));

        Assert.Equal(expected, result.Release?.Url);
    }

    /// <summary>
    /// A rolling tag republished tonight is not a new release to compare against — the version is. Two nightlies of
    /// the same tag differ by their run number, and the higher one is the update.
    /// </summary>
    [Fact]
    public async Task ANightlyBuild_IsOfferedTheNightlyWithTheHigherRun()
    {
        var result = await Check(
            UpdateChannel.Nightly,
            new Feed(Package("0.8.0-nightly.4"), Package("0.8.0-nightly.9")),
            installed: "0.8.0-nightly.5");

        Assert.Equal("0.8.0-nightly.9", result.Release?.Version);
    }

    /// <summary>Nothing newer on the channel is an "up to date", and that is the only thing that may report one.</summary>
    [Fact]
    public async Task NothingNewerOnTheChannel_IsUpToDate()
    {
        var result = await Check(UpdateChannel.Stable, new Feed(Package("0.7.0")));

        Assert.Null(result.Release);
        Assert.Null(result.Failure);
    }

    /// <summary>
    /// A feed that cannot be reached is a failure with a reason. Reporting "you are up to date" instead is a lie the
    /// operator has every reason to believe, and they would believe it until something else broke.
    /// </summary>
    [Fact]
    public async Task AFeedThatFails_SaysSo_AndNeverThatYouAreUpToDate()
    {
        var result = await Check(UpdateChannel.Stable, new Feed { Fails = true });

        Assert.NotNull(result.Failure);
        Assert.Null(result.Release);
    }

    /// <summary>The same, for a feed that answers too slowly to be waited on rather than one that answers wrongly.</summary>
    [Fact]
    public async Task AFeedThatNeverAnswers_GivesUp_AndNeverSaysYouAreUpToDate()
    {
        var result = await Check(UpdateChannel.Stable, new Feed { Hangs = true }, patience: TimeSpan.FromMilliseconds(50));

        Assert.NotNull(result.Failure);
        Assert.Null(result.Release);
    }

    /// <summary>
    /// A copy nobody installed — a checkout, a tarball, a distribution's package — has no installation to compare a
    /// feed against. It is told that, in those words: an ordinary state, not an error to go hunting for.
    /// </summary>
    [Fact]
    public async Task ACopyTheInstallerNeverPlaced_IsToldItCannotLook()
    {
        var result = await VelopackUpdateService.CheckAsync(
            UpdateChannel.Stable,
            _ => new Feed(),
            locator: null,
            NullLogger.Instance,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Null(result.Release);
        Assert.Contains("cannot look for updates", result.Failure);
    }

    /// <summary>
    /// The nightly is published as a GitHub pre-release, so a source that does not ask for pre-releases cannot see
    /// it. This is the half of "a stable install is never offered a nightly" that the channel name does not cover.
    /// </summary>
    [Theory]
    [InlineData(UpdateChannel.Stable, false)]
    [InlineData(UpdateChannel.Nightly, true)]
    public void TheSource_AsksForPreReleasesOnlyOnTheNightlyChannel(UpdateChannel channel, bool expected) =>
        Assert.Equal(expected, Assert.IsType<GithubSource>(VelopackUpdateService.Source(channel)).Prerelease);

    private Task<UpdateCheckResult> Check(
        UpdateChannel channel,
        Feed feed,
        string installed = "0.8.0",
        TimeSpan? patience = null) =>
        VelopackUpdateService.CheckAsync(
            channel,
            _ => feed,
            new TestVelopackLocator("AI-Cockpit", installed, _packages),
            NullLogger.Instance,
            patience ?? TimeSpan.FromSeconds(5),
            CancellationToken.None);

    private static VelopackAsset Package(string version, string notes = "") => new()
    {
        PackageId = "AI-Cockpit",
        Version = SemanticVersion.Parse(version),
        Type = VelopackAssetType.Full,
        FileName = $"AI-Cockpit-{version}-full.nupkg",
        NotesMarkdown = notes,
    };

    /// <summary>Stands in for the release feed, and remembers what it was asked for — which is the assertion.</summary>
    private sealed class Feed(params VelopackAsset[] assets) : IUpdateSource
    {
        public string? AskedFor { get; private set; }

        public bool Fails { get; init; }

        public bool Hangs { get; init; }

        public async Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger,
            string? appId,
            string channel,
            Guid? stagingId = null,
            VelopackAsset? latestLocalRelease = null)
        {
            AskedFor = channel;

            if (Hangs)
            {
                await Task.Delay(Timeout.Infinite);
            }

            return Fails
                ? throw new HttpRequestException("the feed was unreachable")
                : new VelopackAssetFeed { Assets = assets };
        }

        public Task DownloadReleaseEntry(
            IVelopackLogger logger,
            VelopackAsset releaseEntry,
            string localFile,
            Action<int> progress,
            CancellationToken cancelToken = default) =>
            throw new NotSupportedException("a check never downloads — applying an update is AC-388's half.");
    }
}
