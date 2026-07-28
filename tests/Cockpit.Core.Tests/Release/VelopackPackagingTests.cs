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
    /// <para>
    /// Asserted against the release-creating step alone, not the file. Over the whole file this passes on
    /// <c>nightly.yml</c> no matter what, because the same path appears in the artifact upload — so dropping it from
    /// the release command would have left the feed off the release with the guard still green.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void TheFeed_IsAttachedToTheRelease(string workflow)
    {
        Assert.Contains("artifacts-velopack/*", _ReleaseStep(workflow), StringComparison.Ordinal);
    }

    /// <summary>
    /// The step that creates the release. Named in the nightly, anonymous in the release workflow — where it is the
    /// action's own <c>uses:</c> line — so each is found by what identifies it there.
    /// </summary>
    private static string _ReleaseStep(string workflow) => workflow switch
    {
        "nightly.yml" => _Step(workflow, "Publish the rolling nightly"),
        "release.yml" => _StepAt(workflow, line => line.Trim() == "- uses: softprops/action-gh-release@v2"),
        _ => throw new ArgumentOutOfRangeException(nameof(workflow), workflow, "no release step is known for this workflow"),
    };

    /// <summary>
    /// One step of a workflow, by its name. Steps sit at six spaces and everything within them is indented further,
    /// so the next line at that indent ends the block.
    /// </summary>
    private static string _Step(string workflow, string stepName) =>
        _StepAt(workflow, line => line.Trim() == $"- name: {stepName}");

    /// <summary>
    /// The block belonging to the first line the predicate matches. A job key also ends it — without that, a step
    /// that happens to be its job's last would swallow the whole of the next job, and an assertion meant for one
    /// step would quietly be reading another's.
    /// </summary>
    private static string _StepAt(string workflow, Func<string, bool> isStart)
    {
        var lines = File.ReadAllLines(_WorkflowPath(workflow));
        var start = Array.FindIndex(lines, line => isStart(line));
        Assert.True(start >= 0, $"{workflow} has no step matching the expected start line");

        var block = lines.Skip(start + 1)
            .TakeWhile(line => !Regex.IsMatch(line, @"^      - \S") && !Regex.IsMatch(line, @"^ {0,2}\S"))
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
