using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class ProjectResourcePortabilityClassifierTests
{
    // Raymond's decision (AC-605): "~user/" is not a supported anchor form — a POSIX shell's "someone else's home"
    // expansion .NET's own path APIs know nothing about. Reads as an ordinary repo-relative-shaped reference
    // instead, the same as any other text this classifier does not recognise a shape for.

    [WindowsFact("A drive-letter path is only an absolute path on Windows.")]
    public void Classify_WindowsDriveLetterPath_IsAbsoluteNotAOneCharacterScheme()
    {
        // Mirrors Cockpit.Core.Projects.ProjectMemoryRef.TryParse's own guard: "C:\Users\raymond" puts a colon at
        // index 1, which without the two-character floor would misparse as scheme "C".
        Assert.Equal(ProjectResourcePortability.Absolute, ProjectResourcePortabilityClassifier.Classify(@"C:\Users\raymond\Notes.md"));
    }

    // AC-244 (coordinator probe, 2026-08-02): Classify judges the shape of what is there, it does not special-case
    // a blank reference — that gate belongs to Create/Apply, which know a blank reference means "nothing to write"
    // rather than "a specific unportable shape". Pinned here so that division of responsibility does not drift.

    [Theory]
    [InlineData(ProjectResourcePortability.RepoRelative, true)]
    [InlineData(ProjectResourcePortability.PluginSource, true)]
    [InlineData(ProjectResourcePortability.AnchorRelative, true)]
    [InlineData(ProjectResourcePortability.Absolute, false)]
    public void IsPortable_EachShape_MatchesTheAC605Decision(ProjectResourcePortability portability, bool expected)
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
