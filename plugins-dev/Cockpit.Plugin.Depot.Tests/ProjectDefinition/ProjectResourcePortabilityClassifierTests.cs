using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class ProjectResourcePortabilityClassifierTests
{
    [Theory]
    [InlineData("docs/CONVENTIONS.md")]
    [InlineData("Notes.md")]
    [InlineData("sub/folder/file.txt")]
    public void Classify_RepoRelativePath_IsRepoRelative(string reference)
    {
        Assert.Equal(ProjectResourcePortability.RepoRelative, ProjectResourcePortabilityClassifier.Classify(reference));
    }

    [Theory]
    [InlineData("~/Notes/CONVENTIONS.md")]
    [InlineData("~")]
    public void Classify_TildeAnchoredPath_IsAnchorRelative(string reference)
    {
        Assert.Equal(ProjectResourcePortability.AnchorRelative, ProjectResourcePortabilityClassifier.Classify(reference));
    }

    [Theory]
    [InlineData("depot:payroll-processor")]
    [InlineData("depot:slug/path/to/file.md")]
    [InlineData("github:owner/repo")]
    public void Classify_SchemeReference_IsPluginSource(string reference)
    {
        Assert.Equal(ProjectResourcePortability.PluginSource, ProjectResourcePortabilityClassifier.Classify(reference));
    }

    [Theory]
    [InlineData("/home/raymond/Notes/CONVENTIONS.md")]
    public void Classify_PosixAbsolutePath_IsAbsolute(string reference)
    {
        Assert.Equal(ProjectResourcePortability.Absolute, ProjectResourcePortabilityClassifier.Classify(reference));
    }

    [Fact]
    public void Classify_WindowsDriveLetterPath_IsAbsoluteNotAOneCharacterScheme()
    {
        // Mirrors Cockpit.Core.Projects.ProjectMemoryRef.TryParse's own guard: "C:\Users\raymond" puts a colon at
        // index 1, which without the two-character floor would misparse as scheme "C".
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(ProjectResourcePortability.Absolute, ProjectResourcePortabilityClassifier.Classify(@"C:\Users\raymond\Notes.md"));
    }

    [Fact]
    public void Classify_ColonWithNothingAfterIt_IsNotAPluginSource()
    {
        Assert.NotEqual(ProjectResourcePortability.PluginSource, ProjectResourcePortabilityClassifier.Classify("weirdname:"));
    }

    // AC-244 (coordinator probe, 2026-08-02): Classify judges the shape of what is there, it does not special-case
    // a blank reference — that gate belongs to Create/Apply, which know a blank reference means "nothing to write"
    // rather than "a specific unportable shape". Pinned here so that division of responsibility does not drift.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_BlankReference_IsRepoRelative_TheBlankGateLivesInCreateNotHere(string reference)
    {
        Assert.Equal(ProjectResourcePortability.RepoRelative, ProjectResourcePortabilityClassifier.Classify(reference));
    }

    [Theory]
    [InlineData(ProjectResourcePortability.RepoRelative, true)]
    [InlineData(ProjectResourcePortability.PluginSource, true)]
    [InlineData(ProjectResourcePortability.AnchorRelative, false)]
    [InlineData(ProjectResourcePortability.Absolute, false)]
    public void IsPortable_EachShape_MatchesTheAC244Decision(ProjectResourcePortability portability, bool expected)
    {
        Assert.Equal(expected, ProjectResourcePortabilityClassifier.IsPortable(portability));
    }

    [Theory]
    [InlineData(ProjectResourcePortability.RepoRelative, "repo-relative")]
    [InlineData(ProjectResourcePortability.AnchorRelative, "anchor-relative")]
    [InlineData(ProjectResourcePortability.PluginSource, "plugin-source")]
    [InlineData(ProjectResourcePortability.Absolute, "absolute")]
    public void ToWireValue_EachShape_MatchesTheDocumentedWireName(ProjectResourcePortability portability, string expected)
    {
        Assert.Equal(expected, ProjectResourcePortabilityClassifier.ToWireValue(portability));
    }
}
