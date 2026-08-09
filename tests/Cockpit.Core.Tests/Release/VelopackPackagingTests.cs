using System.Text.RegularExpressions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Updates;

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
    /// Windows has exactly one installation form: the Velopack Setup built from the directory publish (AC-496).
    /// The Inno installer and the single-file publish that fed it are gone — this is the guard that keeps them
    /// gone, since nothing else fails loudly if a later edit brings either back.
    /// <para>
    /// Mutation-proven: reintroducing the "Windows installer" step (restoring
    /// <c>scripts/package-windows-installer.ps1</c>) or the "Publish the single-file Windows build" step turns
    /// this red.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void TheWorkflow_CarriesNoLegacyWindowsInstaller(string workflow)
    {
        var text = _Workflow(workflow);

        Assert.DoesNotContain("package-windows-installer.ps1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("windows-installer.iss", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows installer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Publish the single-file Windows build", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishSingleFile", text, StringComparison.Ordinal);
        Assert.DoesNotContain("win-x64-setup.exe\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The legacy scripts themselves are deleted, not merely unreferenced — a file left behind is a file someone
    /// re-wires the workflow back to.
    /// </summary>
    [Fact]
    public void TheLegacyInnoScripts_AreDeleted()
    {
        var repoRoot = _RepoRoot();
        Assert.False(File.Exists(Path.Combine(repoRoot, "scripts", "package-windows-installer.ps1")));
        Assert.False(File.Exists(Path.Combine(repoRoot, "scripts", "windows-installer.iss")));
    }

    /// <summary>
    /// The feed files (`RELEASES-*`, `releases.*.json`, `*-full.nupkg`) have to ride on the release for the updater
    /// to work, but they read as noise on a human download list unless the notes say so explicitly (AC-496).
    /// </summary>
    [Fact]
    public void TheReleaseNotes_MarkTheFeedFilesAsMachineryNotDownloads()
    {
        var notes = _Step("release.yml", "Append what each platform needs on first run");

        Assert.Contains("RELEASES-*", notes, StringComparison.Ordinal);
        Assert.Contains("releases.*.json", notes, StringComparison.Ordinal);
        Assert.Contains("*-full.nupkg", notes, StringComparison.Ordinal);
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
    /// The name the app asks its feed for has to be the name the workflow packed under, or the check reads a channel
    /// nobody publishes to and finds nothing — quietly, and only on an installed copy, which is the last place you
    /// would look (AC-387). The two live a repository apart, so this puts them in one assertion.
    /// </summary>
    [Theory]
    [InlineData("release.yml", "stable", UpdateChannel.Stable)]
    [InlineData("nightly.yml", "nightly", UpdateChannel.Nightly)]
    public void TheChannelTheAppAsksFor_IsTheOneTheWorkflowPacked(string workflow, string stream, UpdateChannel channel)
    {
        var pack = _Step(workflow, "Pack the Velopack release");

        foreach (var platform in new[] { "win", "osx", "linux" })
        {
            // The workflow builds the name from two pieces — "platform=win;" and --channel "$platform-<stream>" — so
            // this composes the same two and requires the app to arrive at the result.
            Assert.Contains($"platform={platform};", pack, StringComparison.Ordinal);
            Assert.Contains($"--channel \"$platform-{stream}\"", pack, StringComparison.Ordinal);

            Assert.Equal($"{platform}-{stream}", UpdateChannelName.For(platform, channel));
        }
    }

    /// <summary>
    /// And the platform the running cockpit names itself is one of those three. Without this the rule above could be
    /// satisfied by a table nothing consults.
    /// </summary>
    [Fact]
    public void TheRunningCockpit_NamesItselfOneOfThePlatformsThatArePacked() =>
        Assert.Contains(UpdateChannelName.Platform(), new[] { "win", "osx", "linux" });

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
    /// A release page is where somebody stands when their machine refuses the download, so the release notes answer
    /// all three refusals rather than only the one macOS gives (AC-389). Asserted per platform: the note grew out of
    /// a macOS-only one, and the way it would shrink back is a platform quietly going missing.
    /// <para>
    /// macOS carries two, and the distinction between them is the point (AC-663). The install script is the answer,
    /// because a bundle fetched with curl never gets the quarantine flag that makes Gatekeeper look. The fallback is
    /// <c>xattr -dr com.apple.quarantine</c> and specifically <em>not</em> <c>xattr -cr</c>, which the notes used to
    /// recommend: that clears every extended attribute, and <c>codesign</c> keeps each managed assembly's signature
    /// in one, so it leaves the bundle reading "code object is not signed at all" — measured, and the identity macOS
    /// hangs the microphone permission on. Asserting the exact flag is what stops it being shortened back.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("SmartScreen")]
    [InlineData("scripts/install-macos.sh")]
    [InlineData("xattr -dr com.apple.quarantine /Applications/AI-Cockpit.app")]
    [InlineData("chmod +x AI-Cockpit-*.AppImage")]
    public void TheReleaseNotes_SayWhatEachPlatformNeedsOnFirstRun(string instruction) =>
        Assert.Contains(instruction, _Step("release.yml", "Append what each platform needs on first run"), StringComparison.Ordinal);

    /// <summary>
    /// The one-time switch for somebody already running the Inno installation (AC-389). Velopack installs per-user
    /// and does not adopt the copy in Program Files, so without this the old installation simply stops being updated
    /// — silently, because it goes on checking and finding nothing it can reach.
    /// </summary>
    [Fact]
    public void TheReleaseNotes_TellAnExistingWindowsInstallToSwitchOnce()
    {
        var notes = _Step("release.yml", "Append what each platform needs on first run");

        Assert.Contains("ai-cockpit-…-win-x64-setup.exe", notes, StringComparison.Ordinal);
        Assert.Contains("Settings → Apps", notes, StringComparison.Ordinal);

        // The reason the switch is safe, and the one thing they will actually worry about. The folder is named from
        // CockpitBuild.ProductionStateFolder rather than spelled out, so a rename cannot leave this pointing nowhere.
        Assert.Contains($@"%APPDATA%\{CockpitBuild.ProductionStateFolder}", notes, StringComparison.Ordinal);
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

    /// <summary>
    /// The repo root, found the same way <see cref="_WorkflowPath"/> finds a workflow — by walking up from the
    /// test output until <c>.github/workflows</c> shows up beneath a candidate directory.
    /// </summary>
    private static string _RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".github", "workflows")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("No folder above the test output holds .github/workflows — this test reads the repo it belongs to.");
    }

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
