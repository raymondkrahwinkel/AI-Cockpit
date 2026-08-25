using Cockpit.Core.Projects;
using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// AC-605 criterion 4: the host (`ProjectResourcePathPortability.ClassifyScope`) and this plugin
// (`ProjectResourcePortabilityClassifier.Classify`) must agree on every reference shape — they cannot share
// code (a plugin must not reference `Cockpit.Core`, AC-244), so both run against the same table here.
public class ProjectResourceScopeParityTests
{
    // Maps the plugin's own four-shape vocabulary onto the host's — see ProjectResourceScope's own remarks on why
    // the two enums have different names for the same four concepts (each side's names read as what its own type
    // means, not as a copy of the other's wire vocabulary).
    private static readonly Dictionary<ProjectResourceScope, ProjectResourcePortability> _ScopeToPortability = new()
    {
        [ProjectResourceScope.Repo] = ProjectResourcePortability.RepoRelative,
        [ProjectResourceScope.Home] = ProjectResourcePortability.AnchorRelative,
        [ProjectResourceScope.Instance] = ProjectResourcePortability.PluginSource,
        [ProjectResourceScope.Machine] = ProjectResourcePortability.Absolute,
    };

    public static IEnumerable<object[]> References()
    {
        // Repo-relative shapes.
        yield return ["docs/CONVENTIONS.md"];
        yield return ["Notes.md"];
        yield return ["sub/folder/file.txt"];
        yield return ["."];

        // Home-anchored shapes (AC-605: the only two supported forms).
        yield return ["~"];
        yield return ["~/Notes/CONVENTIONS.md"];
        yield return ["~//Notes/CONVENTIONS.md"];
        yield return ["~/../../etc/passwd"];

        // "~user/" is deliberately NOT an anchor form (Raymond's decision) — both sides must read it as ordinary
        // (repo-relative-shaped) text, which is itself a parity claim worth pinning, not just AnchorRelative cases.
        yield return ["~henk/x"];
        yield return ["~x"];

        // Plugin-source shapes.
        yield return ["depot:cockpit"];
        yield return ["depot:slug/path/to/file.md"];
        yield return ["github:owner/repo"];
        yield return ["weirdname:"]; // no value after the colon — not a scheme either side recognises.

        // Absolute (machine) shapes — POSIX-rooted; a Windows-rooted case is added conditionally below.
        yield return ["/home/raymond/Notes/CONVENTIONS.md"];
        yield return ["/etc/passwd"];
    }

    [Theory]
    [MemberData(nameof(References))]
    public void ClassifyScope_AndClassify_AgreeOnEveryReferenceShape(string reference)
    {
        var hostScope = ProjectResourcePathPortability.ClassifyScope(reference);
        var pluginPortability = ProjectResourcePortabilityClassifier.Classify(reference);

        Assert.NotNull(hostScope);
        Assert.Equal(_ScopeToPortability[hostScope!.Value], pluginPortability);
    }

    [Fact]
    public void ClassifyScope_AndClassify_AgreeOnAWindowsDriveLetterPath()
    {
        // Windows-rooted only on Windows — Path.IsPathFullyQualified is itself platform-specific (both classes'
        // own remarks document this), so this reference is Repo/RepoRelative on Linux, not a divergence.
        const string reference = @"C:\Users\raymond\Notes.md";

        var hostScope = ProjectResourcePathPortability.ClassifyScope(reference);
        var pluginPortability = ProjectResourcePortabilityClassifier.Classify(reference);

        Assert.NotNull(hostScope);
        Assert.Equal(_ScopeToPortability[hostScope!.Value], pluginPortability);
        Assert.Equal(
            OperatingSystem.IsWindows() ? ProjectResourceScope.Machine : ProjectResourceScope.Repo,
            hostScope);
    }

    // Every `ProjectResourceScope` a real reference can produce must also be portable-or-not identically on both sides — the other half of criterion 4, since Classify's `IsPortable`/wire vocabulary has no direct host equivalent to compare Scope against otherwise.
    [Theory]
    [InlineData(ProjectResourceScope.Repo, true)]
    [InlineData(ProjectResourceScope.Home, true)]
    [InlineData(ProjectResourceScope.Instance, true)]
    [InlineData(ProjectResourceScope.Machine, false)]
    public void EveryScope_MapsToTheSamePortabilityDecisionAsItsPluginCounterpart(ProjectResourceScope scope, bool expectedPortable)
    {
        Assert.Equal(expectedPortable, ProjectResourcePortabilityClassifier.IsPortable(_ScopeToPortability[scope]));
    }
}
