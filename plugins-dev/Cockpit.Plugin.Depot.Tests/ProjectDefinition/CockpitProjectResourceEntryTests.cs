using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

/// <summary>
/// <see cref="CockpitProjectResourceEntry.Create"/> — the decision that an absolute reference is left out of a
/// written definition rather than shipped or the write refused outright (AC-244, narrowed by AC-605: an
/// anchor-relative reference is portable now, so it is written like any other portable row — see
/// <see cref="Create_AnchorRelativeReference_ReturnsARowWithThatPortability"/>).
/// </summary>
public class CockpitProjectResourceEntryTests
{
    [Fact]
    public void Create_RepoRelativeReference_ReturnsARowWithThatPortability()
    {
        var entry = CockpitProjectResourceEntry.Create("Instructions", "docs/CONVENTIONS.md", "Conventies");

        Assert.NotNull(entry);
        Assert.Equal("Instructions", entry.Role);
        Assert.Equal("docs/CONVENTIONS.md", entry.Reference);
        Assert.Equal("Conventies", entry.Label);
        Assert.Equal("repo-relative", entry.Portability);
    }

    [Fact]
    public void Create_PluginSourceReference_ReturnsARowWithThatPortability()
    {
        var entry = CockpitProjectResourceEntry.Create("Memory", "depot:payroll-processor");

        Assert.NotNull(entry);
        Assert.Equal("plugin-source", entry.Portability);
    }

    [Fact]
    public void Create_AbsoluteReference_ReturnsNull()
    {
        Assert.Null(CockpitProjectResourceEntry.Create("Reference", "/home/raymond/Notes/private.md"));
    }

    [Fact]
    public void Create_AnchorRelativeReference_ReturnsARowWithThatPortability()
    {
        var entry = CockpitProjectResourceEntry.Create("Reference", "~/Notes/private.md");

        Assert.NotNull(entry);
        Assert.Equal("~/Notes/private.md", entry.Reference);
        Assert.Equal("anchor-relative", entry.Portability);
    }

    [Fact]
    public void Create_NoLabel_LeavesLabelNull()
    {
        var entry = CockpitProjectResourceEntry.Create("Memory", "depot:cockpit");

        Assert.Null(entry!.Label);
    }

    // AC-244 (coordinator probe, 2026-08-02): a blank reference used to classify as RepoRelative and get written
    // as a portable row pointing at nothing. Create refuses it before Classify ever sees it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankReference_ReturnsNull(string reference)
    {
        Assert.Null(CockpitProjectResourceEntry.Create("Memory", reference));
    }

    // --- AC-612: a reference ProjectResourceSecretPathHeuristic recognises is refused outright, whatever shape it is otherwise ---

    [Fact]
    public void Create_AnAnchorRelativeSecretPathReference_ReturnsNullEvenThoughItIsOtherwisePortable()
    {
        // Portable by shape alone (anchor-relative, AC-605) — the secret check has to run independently of the
        // portability gate, since that gate alone would let this row through.
        Assert.Null(CockpitProjectResourceEntry.Create("Instructions", "~/.ssh/id_rsa"));
    }

    [Fact]
    public void Create_AnAbsoluteSecretPathReference_ReturnsNull()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Null(CockpitProjectResourceEntry.Create("Reference", Path.Combine(home, ".aws", "credentials")));
    }

    [Fact]
    public void Create_APublicKeyReference_IsNotTreatedAsSecret()
    {
        var entry = CockpitProjectResourceEntry.Create("Instructions", "~/.ssh/id_rsa.pub");

        Assert.NotNull(entry);
        Assert.Equal("anchor-relative", entry.Portability);
    }
}
