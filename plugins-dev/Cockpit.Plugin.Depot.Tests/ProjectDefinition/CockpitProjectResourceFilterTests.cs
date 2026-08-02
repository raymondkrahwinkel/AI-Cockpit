using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectResourceFilterTests
{
    [Fact]
    public void Apply_MixOfAllFourShapes_PortableCarriesThreeAndDroppedCarriesTheOneAbsoluteRow()
    {
        var result = CockpitProjectResourceFilter.Apply(
        [
            ("Memory", "depot:cockpit", null),
            ("Instructions", "docs/CONVENTIONS.md", "Conventies"),
            ("Reference", "~/Notes/private.md", null),
            ("Reference", "/home/raymond/private-notes.md", null),
        ]);

        // AC-605: AnchorRelative is portable now — it travels to everyone with the instance, resolved against
        // whoever opens the project, so it joins the other two portable rows instead of being dropped.
        Assert.Equal(3, result.Portable.Count);
        Assert.Equal(ProjectResourcePortability.Absolute, Assert.Single(result.Dropped).Portability);
    }

    [Fact]
    public void Apply_DroppedRow_CarriesTheOriginalRoleAndLabel()
    {
        var result = CockpitProjectResourceFilter.Apply([("Reference", "/home/raymond/private-notes.md", "My private notes")]);

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

    /// <summary>AC-605: reverses the AC-244-era divergence this test used to pin — the host editor and this filter now agree that a "~/..." reference travels, so it is carried, not dropped.</summary>
    [Fact]
    public void Apply_AnchorRelativeReference_IsPortableNotDropped()
    {
        var result = CockpitProjectResourceFilter.Apply([("Reference", "~/Notes/private.md", null)]);

        var portable = Assert.Single(result.Portable);
        Assert.Equal("anchor-relative", portable.Portability);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void Apply_AbsoluteReference_IsStillDropped()
    {
        var result = CockpitProjectResourceFilter.Apply([("Reference", "/home/raymond/private-notes.md", null)]);

        var dropped = Assert.Single(result.Dropped);
        Assert.Equal(ProjectResourcePortability.Absolute, dropped.Portability);
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
