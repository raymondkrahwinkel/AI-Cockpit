using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectResourceFilterTests
{
    [Fact]
    public void Apply_MixOfAllFourShapes_PortableCarriesTwoAndDroppedCarriesTheOtherTwoWithReasons()
    {
        var result = CockpitProjectResourceFilter.Apply(
        [
            ("Memory", "depot:cockpit", null),
            ("Instructions", "docs/CONVENTIONS.md", "Conventies"),
            ("Reference", "~/Notes/private.md", null),
            ("Reference", "/home/raymond/private-notes.md", null),
        ]);

        Assert.Equal(2, result.Portable.Count);
        Assert.Equal(2, result.Dropped.Count);
        Assert.Equal("~/Notes/private.md", Assert.Single(result.Dropped, d => d.Reference == "~/Notes/private.md").Reference);
        Assert.Equal(ProjectResourcePortability.AnchorRelative, Assert.Single(result.Dropped, d => d.Reference == "~/Notes/private.md").Portability);
        Assert.Equal(ProjectResourcePortability.Absolute, Assert.Single(result.Dropped, d => d.Reference == "/home/raymond/private-notes.md").Portability);
    }

    [Fact]
    public void Apply_DroppedRow_CarriesTheOriginalRoleAndLabel()
    {
        var result = CockpitProjectResourceFilter.Apply([("Reference", "~/Notes/private.md", "My private notes")]);

        var dropped = Assert.Single(result.Dropped);
        Assert.Equal("Reference", dropped.Role);
        Assert.Equal("My private notes", dropped.Label);
    }

    [Fact]
    public void Apply_NoRows_ReturnsBothEmpty()
    {
        var result = CockpitProjectResourceFilter.Apply([]);

        Assert.Empty(result.Portable);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void Apply_AllPortable_DroppedIsEmpty()
    {
        var result = CockpitProjectResourceFilter.Apply([("Memory", "depot:cockpit", null)]);

        Assert.Single(result.Portable);
        Assert.Empty(result.Dropped);
    }

    // AC-244 finding (2026-08-02): host.ProjectResourcePathPortability.IsMachineBound shows the editor no warning
    // for a "~/..." reference (Path.IsPathFullyQualified rejects it), while this filter still drops it — this test
    // pins the drop side of that measured divergence so a future fix does not silently change it unnoticed.
    [Fact]
    public void Apply_AnchorRelativeReference_IsDroppedEvenThoughTheHostEditorShowsNoWarningForIt()
    {
        var result = CockpitProjectResourceFilter.Apply([("Reference", "~/Notes/private.md", null)]);

        var dropped = Assert.Single(result.Dropped);
        Assert.Equal(ProjectResourcePortability.AnchorRelative, dropped.Portability);
    }

    // AC-244 (coordinator probe, 2026-08-02): a blank reference is a different reason than an unportable shape —
    // Portability is null here rather than the misleading RepoRelative Classify would report for the bare text.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_BlankReference_IsDroppedWithNullPortabilityRatherThanAMisleadingShape(string reference)
    {
        var result = CockpitProjectResourceFilter.Apply([("Reference", reference, null)]);

        var dropped = Assert.Single(result.Dropped);
        Assert.Equal(reference, dropped.Reference);
        Assert.Null(dropped.Portability);
    }
}
