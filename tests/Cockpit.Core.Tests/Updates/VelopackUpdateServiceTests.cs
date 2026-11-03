using System.Security.Cryptography;
using System.Text;
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
    /// The nightly workflow deletes its release and its tag and makes them again every run, so the feed after a
    /// retag carries one nightly, not a history — and the tag it hangs on is the same string it was yesterday. What
    /// tells last night's install from tonight's build is therefore the version and nothing else, which is exactly
    /// what the rolling tag cannot do. Modelled the way the workflow leaves it: one asset, a higher run number.
    /// </summary>
    [Fact]
    public async Task AfterTheNightlyTagIsRemade_TheRebuiltNightlyIsStillSeenAsNewer()
    {
        var result = await Check(UpdateChannel.Nightly, new Feed(Package("0.8.0-nightly.9")), installed: "0.8.0-nightly.5");

        Assert.Equal("0.8.0-nightly.9", result.Release?.Version);
    }

    /// <summary>
    /// Nothing newer on the channel is an "up to date", and that is the only thing that may report one. The last
    /// row is a nightly retag offering the build that is already installed: republishing the tag is not an update.
    /// </summary>
    [Theory]
    [InlineData(UpdateChannel.Nightly, "0.8.0-nightly.4", "0.8.0-nightly.5")]
    [InlineData(UpdateChannel.Nightly, "0.8.0-nightly.9", "0.8.0-nightly.9")]
    public async Task NothingNewerOnTheChannel_IsUpToDate(UpdateChannel channel, string offered, string installed)
    {
        var result = await Check(channel, new Feed(Package(offered)), installed);

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

    /// <summary>
    /// The nightly is published as a GitHub pre-release, so a source that does not ask for pre-releases cannot see
    /// it. This is the half of "a stable install is never offered a nightly" that the channel name does not cover.
    /// </summary>
    [Theory]
    [InlineData(UpdateChannel.Stable, false)]
    [InlineData(UpdateChannel.Nightly, true)]
    public void TheSource_AsksForPreReleasesOnlyOnTheNightlyChannel(UpdateChannel channel, bool expected) =>
        Assert.Equal(expected, Assert.IsType<GithubSource>(VelopackUpdateService.Source(channel)).Prerelease);

    /// <summary>
    /// The feed is read anonymously, and that is what keeps drafts out of it (AC-462): GitHub lists draft releases
    /// only to callers with push access. The cockpit used to drop drafts itself and cannot any more — the release
    /// model Velopack reads into carries no draft field — so this is the whole of the protection, and it is one
    /// argument away from being gone. Pinned here because the reason someone would add a token is the rate limit,
    /// which gives no hint that drafts are riding on the answer.
    /// </summary>
    [Fact]
    public void TheFeedIsReadAnonymously_WhichIsWhatKeepsDraftReleasesOutOfIt() =>
        Assert.Null(VelopackUpdateService.AccessToken);

    /// <summary>
    /// A transfer that breaks partway leaves the app exactly as it found it: a failure with a reason, and — this is
    /// the assertion that matters — nothing recorded as "ready to apply", so a stray "Update now" click afterwards
    /// cannot restart into a half-fetched build.
    /// </summary>
    [Fact]
    public async Task ATransferThatBreaksPartway_LeavesNothingReadyToApply()
    {
        var service = new VelopackUpdateService(NullLogger<VelopackUpdateService>.Instance);

        var result = await service.DownloadAsync(
            UpdateChannel.Stable,
            source: _ => new Feed(Package("0.9.0")) { DownloadFails = true },
            new TestVelopackLocator("AI-Cockpit", "0.8.0", _packages),
            NullLogger.Instance,
            TimeSpan.FromSeconds(5),
            progress: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);

        // Nothing was fetched, so applying must be a no-op rather than throw or restart into a bad build.
        service.ApplyDownloadedUpdateAndRestart();
        Assert.False(service.RequestUpdateOnNextStart());
    }

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

    private static VelopackAsset Package(string version, string notes = "")
    {
        var fileName = $"AI-Cockpit-{version}-full.nupkg";
        var bytes = PackageBytes(fileName);

        return new VelopackAsset
        {
            PackageId = "AI-Cockpit",
            Version = SemanticVersion.Parse(version),
            Type = VelopackAssetType.Full,
            FileName = fileName,
            NotesMarkdown = notes,
            // Velopack verifies what it downloads against these before calling it done, so a fixture whose bytes do
            // not match its own advertised hash/size would fail for a reason that has nothing to do with the test —
            // weakening that verification in production code to dodge it was the one thing not on the table (AC-388).
            SHA1 = Convert.ToHexString(SHA1.HashData(bytes)),
            Size = bytes.Length,
        };
    }

    /// <summary>Deterministic fixture bytes for a package, keyed by the file name a <see cref="Package"/> was given — so <see cref="Feed.DownloadReleaseEntry"/> can hand back exactly what <see cref="Package"/> promised.</summary>
    private static byte[] PackageBytes(string fileName) => Encoding.UTF8.GetBytes($"contents of {fileName}");

    /// <summary>Stands in for the release feed, and remembers what it was asked for — which is the assertion.</summary>
    private sealed class Feed(params VelopackAsset[] assets) : IUpdateSource
    {
        public string? AskedFor { get; private set; }

        public bool Fails { get; init; }

        public bool Hangs { get; init; }

        /// <summary>Makes the download itself fail partway (AC-388), distinct from <see cref="Fails"/>, which fails the feed lookup that happens before any download starts.</summary>
        public bool DownloadFails { get; init; }

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

        /// <summary>
        /// The download itself (AC-388): writes the fixture bytes a <see cref="Package"/> promised, through the same
        /// <see cref="IUpdateSource"/> seam <see cref="VelopackUpdateService.DownloadAsync"/> drives a real
        /// <see cref="UpdateManager"/> through, so what is exercised is the manager's own size/checksum verification
        /// rather than a re-implementation of it.
        /// </summary>
        public async Task DownloadReleaseEntry(
            IVelopackLogger logger,
            VelopackAsset releaseEntry,
            string localFile,
            Action<int> progress,
            CancellationToken cancelToken = default)
        {
            if (DownloadFails)
            {
                throw new HttpRequestException("the download was interrupted");
            }

            if (Hangs)
            {
                await Task.Delay(Timeout.Infinite, cancelToken);
            }

            foreach (var percent in new[] { 0, 25, 50, 75 })
            {
                cancelToken.ThrowIfCancellationRequested();
                progress(percent);
                await Task.Yield();
            }

            await File.WriteAllBytesAsync(localFile, PackageBytes(releaseEntry.FileName), cancelToken);
            progress(100);
        }
    }
}
