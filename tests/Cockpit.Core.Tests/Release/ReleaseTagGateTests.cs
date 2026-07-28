using System.Text.RegularExpressions;

namespace Cockpit.Core.Tests.Release;

/// <summary>
/// The release workflow triggers on <c>v*</c>, which matches a great deal more than a release (AC-386). A tag like
/// <c>v0.8.0-rc.1</c> would have built a full release with <c>prerelease: false</c>, taken over GitHub's "latest
/// release", and then had the finalize job roll <c>[Unreleased]</c> away underneath it — leaving the real release
/// that followed with empty notes. The gate job rejects anything that is not a plain <c>vMAJOR.MINOR.PATCH</c>.
/// <para>
/// This reads the workflow as text, the way <see cref="Styles.ThemeHexColorGuardTests"/> reads the sources: the gate
/// is bash running on a GitHub runner, so there is no object to call. What it does have is a regex, and a regex can
/// be lifted out and put through the exact tags the ticket names. Testing the pattern the workflow actually carries —
/// rather than a copy of it declared here — is the whole point: a copy would keep passing after someone loosened
/// the real one.
/// </para>
/// <para>
/// The bash <c>=~</c> operator takes a POSIX ERE, and .NET's engine agrees with it on this pattern's constructs
/// (anchors, character classes, <c>+</c>). A pattern that grew beyond that overlap would need a different check —
/// which is why the extraction below fails loudly rather than falling back to a hardcoded default.
/// </para>
/// </summary>
public class ReleaseTagGateTests
{
    private const string GateJob = "gate";
    private const string ChangelogJob = "changelog";

    /// <summary>
    /// The tags the ticket names, and the only shape that may start a release build.
    /// </summary>
    public static TheoryData<string> RejectedTags() => new("v1.2.3-rc.1", "v1.2", "v1.2.3.4", "version-1.2.3");

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("v0.8.0")]
    [InlineData("v10.20.30")]
    public void TheGatesPattern_AcceptsAPlainVersionTag(string tag)
    {
        Assert.Matches(_GatePattern(), tag);
    }

    [Theory]
    [MemberData(nameof(RejectedTags))]
    public void TheGatesPattern_RejectsAnythingElse(string tag)
    {
        Assert.DoesNotMatch(_GatePattern(), tag);
    }

    /// <summary>
    /// The pattern alone does not say which way the gate points: a condition that dropped its negation would still
    /// carry a correct regex and would fail every release instead of every non-release. So the shape of the test is
    /// asserted too — negated, and failing the run rather than merely reporting.
    /// </summary>
    [Fact]
    public void TheGate_FailsTheRunOnANonMatchingTag()
    {
        var gate = _JobBlock(GateJob);

        Assert.Matches(@"if\s*\[\[\s*!\s*""\$GITHUB_REF_NAME""\s*=~", gate);
        Assert.Contains("exit 1", gate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate is only a gate if the job that reads the changelog waits for it. Without this, a rejected tag would
    /// still have had its <c>[Unreleased]</c> section extracted, and — through publish and finalize — rolled away.
    /// </summary>
    [Fact]
    public void EveryOtherJob_RunsBehindTheGate()
    {
        Assert.Contains($"needs: {GateJob}", _JobBlock(ChangelogJob), StringComparison.Ordinal);
        Assert.Contains($"needs: {ChangelogJob}", _JobBlock("publish"), StringComparison.Ordinal);
        Assert.Contains("needs: publish", _JobBlock("finalize"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A tag name is text an outsider chooses, and <c>${{ }}</c> is pasted into the script before bash parses it —
    /// so a tag carrying a quote would be run rather than compared. The gate reads the environment variable instead.
    /// </summary>
    [Fact]
    public void TheGate_ReadsTheTagFromTheEnvironmentRatherThanAnExpression()
    {
        Assert.DoesNotContain("${{", _JobBlock(GateJob), StringComparison.Ordinal);
    }

    /// <summary>
    /// Lifts the regex out of the gate's <c>=~</c> test. Throws rather than defaulting: a gate that cannot be found
    /// is the failure this whole class exists to report, and a silent fallback would report it as a pass.
    /// </summary>
    private static string _GatePattern()
    {
        var gate = _JobBlock(GateJob);
        var match = Regex.Match(gate, @"""\$GITHUB_REF_NAME""\s*=~\s*(?<pattern>\S+)\s*\]\]");
        Assert.True(match.Success,
            $"the {GateJob} job no longer tests $GITHUB_REF_NAME with =~ — the tag gate is gone or has been rewritten into a form this test cannot read");

        return match.Groups["pattern"].Value;
    }

    /// <summary>
    /// One job's block out of the release workflow. Jobs sit at two spaces under <c>jobs:</c> and everything within
    /// them is indented further, so the next line at that same indent ends the block.
    /// <para>
    /// Comment lines are dropped. Every assertion here is about what the job <em>does</em>, and the comments in this
    /// workflow explain the rule by quoting the form it forbids — prose that would otherwise read as a violation of
    /// the very rule it documents.
    /// </para>
    /// </summary>
    private static string _JobBlock(string jobName)
    {
        var lines = File.ReadAllLines(_ReleaseWorkflowPath());
        var start = Array.FindIndex(lines, line => line == $"  {jobName}:");
        Assert.True(start >= 0, $"release.yml has no '{jobName}' job");

        var block = lines.Skip(start + 1)
            .TakeWhile(line => !Regex.IsMatch(line, @"^  \S"))
            .Where(line => !line.TrimStart().StartsWith('#'));

        return string.Join('\n', block);
    }

    /// <summary>
    /// The workflow this test guards, found by walking up from the test output to the repository that holds it.
    /// </summary>
    private static string _ReleaseWorkflowPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", "release.yml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No folder above the test output holds .github/workflows/release.yml — this test reads the repo it belongs to.");
    }
}
