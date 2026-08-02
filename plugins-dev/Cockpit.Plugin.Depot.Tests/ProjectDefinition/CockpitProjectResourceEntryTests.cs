using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// `CockpitProjectResourceEntry.Create` — the AC-244 decision that an absolute or anchor-relative
// reference is left out of a written definition rather than shipped or the write refused outright.
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
    public void Create_AnchorRelativeReference_ReturnsNull()
    {
        Assert.Null(CockpitProjectResourceEntry.Create("Reference", "~/Notes/private.md"));
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
}
