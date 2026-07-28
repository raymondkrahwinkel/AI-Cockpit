using System.Text.RegularExpressions;

namespace Cockpit.Core.Tests.Release;

/// <summary>
/// The release and nightly workflows build the feed the in-app updater reads (AC-384). None of this can be run from
/// a test — it happens on three runners — but the properties that make the feed usable are all statements in the
/// workflow text, and every one of them is the kind of thing a later edit removes without noticing: the packaging
/// step is far from the publish step it depends on, and nothing fails loudly if they drift apart.
/// <para>
/// Read as text, the way <see cref="ReleaseTagGateTests"/> reads the tag gate. A test cannot tell you the release
/// came out right; it can tell you nobody quietly undid the reasons it would.
/// </para>
/// </summary>
public class VelopackPackagingTests
{
    public static TheoryData<string> Workflows() => new("release.yml", "nightly.yml");

    /// <summary>
    /// Velopack replaces an application <em>folder</em>, so it needs the files as files. A self-extracting single
    /// exe cannot be patched, and the failure would not be a build error — it would be an update that does nothing
    /// on somebody else's machine.
    /// </summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void ThePublishThatIsPacked_IsADirectory(string workflow)
    {
        Assert.DoesNotContain("PublishSingleFile", _Step(workflow, "Publish"), StringComparison.Ordinal);
        Assert.Contains("--packDir \"publish/$RID\"", _Step(workflow, "Pack the Velopack release"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The single-file build still exists, for the portable exe and the Inno installer that both package exactly one
    /// file. It must come from its own output directory: pointed at the directory publish it would overwrite the
    /// very files vpk is about to pack.
    /// </summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void TheSingleFileBuild_HasItsOwnOutput(string workflow)
    {
        Assert.Contains("--output publish/win-x64-singlefile",
            _Step(workflow, "Publish the single-file Windows build"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The tool writes the feed the app's Velopack library reads, so the two are a matched pair. A floating install
    /// would let a runner pick up a newer vpk than the library was tested against, and the mismatch would surface as
    /// a feed the app cannot read rather than as a failing build.
    /// </summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void TheVpkTool_IsPinned(string workflow)
    {
        Assert.Matches(@"dotnet tool install -g vpk --version \d+\.\d+\.\d+", _Step(workflow, "Install vpk"));
    }

    /// <summary>
    /// The channel carries the platform, so one feed can never offer a macOS package to a Windows install. Without
    /// it every platform would publish into the same channel and the last one to finish would win.
    /// </summary>
    [Theory]
    [InlineData("release.yml", "stable")]
    [InlineData("nightly.yml", "nightly")]
    public void TheChannel_NamesThePlatformAndTheStream(string workflow, string stream)
    {
        var pack = _Step(workflow, "Pack the Velopack release");

        Assert.Contains($"--channel \"$platform-{stream}\"", pack, StringComparison.Ordinal);
        foreach (var platform in new[] { "win", "osx", "linux" })
        {
            Assert.Contains($"platform={platform};", pack, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>scripts/pack-sdk.sh</c> writes the plugin SDK's package into <c>artifacts/</c>. Both it and Velopack
    /// produce a <c>.nupkg</c>, so sharing a directory would leave no way to tell which of them the updater is meant
    /// to read — and the release has to carry both.
    /// </summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void TheFeed_IsPackedBesideTheOtherArtifactsRatherThanAmongThem(string workflow)
    {
        var text = _Workflow(workflow);

        Assert.Contains("--outputDir artifacts-velopack", text, StringComparison.Ordinal);
        Assert.Contains("scripts/pack-sdk.sh artifacts", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The feed is only a feed if it reaches the release: the index file is what the updater looks the release up
    /// in. A glob over the artifacts directory alone matches the Velopack directory rather than descending into it,
    /// which is why both are named.
    /// </summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void TheFeed_IsAttachedToTheRelease(string workflow)
    {
        Assert.Contains("artifacts-velopack/*", _Workflow(workflow), StringComparison.Ordinal);
    }

    /// <summary>
    /// One step of a workflow, by its name. Steps sit at six spaces and everything within them is indented further,
    /// so the next line at that indent ends the block.
    /// </summary>
    private static string _Step(string workflow, string stepName)
    {
        var lines = File.ReadAllLines(_WorkflowPath(workflow));
        var start = Array.FindIndex(lines, line => line.Trim() == $"- name: {stepName}");
        Assert.True(start >= 0, $"{workflow} has no step named '{stepName}'");

        var block = lines.Skip(start + 1)
            .TakeWhile(line => !Regex.IsMatch(line, @"^      - \S"))
            .Where(line => !line.TrimStart().StartsWith('#'));

        return string.Join('\n', block);
    }

    private static string _Workflow(string workflow) => File.ReadAllText(_WorkflowPath(workflow));

    private static string _WorkflowPath(string workflow)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", workflow);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No folder above the test output holds .github/workflows/{workflow} — this test reads the repo it belongs to.");
    }
}
