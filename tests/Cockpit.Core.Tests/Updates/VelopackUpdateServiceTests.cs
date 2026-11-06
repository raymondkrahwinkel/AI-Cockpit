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

    /// <summary>
    /// A higher build is offered with the feed's notes; nothing newer is the only thing that may report "up to date".
    /// The nightly workflow remakes its release and tag every run, so the tag is the same string as yesterday and
    /// only the version tells last night's install from tonight's build (third row) — and a retag offering the
    /// installed build is not an update (last row).
    /// </summary>
    [Theory]
    [InlineData(UpdateChannel.Stable, "0.9.0", "0.8.0", true)]
    [InlineData(UpdateChannel.Nightly, "0.8.0-nightly.4", "0.8.0-nightly.5", false)]
    [InlineData(UpdateChannel.Nightly, "0.8.0-nightly.9", "0.8.0-nightly.5", true)]
    [InlineData(UpdateChannel.Nightly, "0.8.0-nightly.9", "0.8.0-nightly.9", false)]
    public async Task OnlyANewerBuildOnTheChannel_IsOffered(UpdateChannel channel, string offered, string installed, bool isOffered)
    {
        var result = await Check(channel, new Feed(Package(offered, "what changed")), installed);

        Assert.Null(result.Failure);
        Assert.Equal(isOffered ? offered : null, result.Release?.Version);
        Assert.Equal(isOffered ? "what changed" : null, result.Release?.Notes);
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
    /// it. And the feed is read anonymously, which is the whole of what keeps drafts out (AC-462): GitHub lists them
    /// only to callers with push access, and Velopack's release model carries no draft field to filter on — so a
    /// token added for the rate limit would quietly start offering drafts.
    /// </summary>
    [Theory]
    [InlineData(UpdateChannel.Stable, false)]
    [InlineData(UpdateChannel.Nightly, true)]
    public void TheSource_AsksForPreReleasesOnlyOnTheNightlyChannel_AndReadsAnonymously(UpdateChannel channel, bool expected)
    {
        Assert.Equal(expected, Assert.IsType<GithubSource>(VelopackUpdateService.Source(channel)).Prerelease);
        Assert.Null(VelopackUpdateService.AccessToken);
    }

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
