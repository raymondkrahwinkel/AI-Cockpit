using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectResourceFilterTests
{
    [Fact]
    public void Apply_MixOfAllFourShapes_AllFourLandInPortableNowNoneAreDropped()
    {
        var absoluteReference = OperatingSystem.IsWindows() ? @"C:\Users\raymond\private-notes.md" : "/home/raymond/private-notes.md";
        var result = CockpitProjectResourceFilter.Apply(
        [
            ("Memory", "depot:cockpit", null),
            ("Instructions", "docs/CONVENTIONS.md", "Conventies"),
            ("Reference", "~/Notes/private.md", null),
            ("Reference", absoluteReference, null),
        ]);

        // AC-605: AnchorRelative is portable now — resolved against whoever opens the project, so it joins
        // the other portable rows. AC-246: the absolute row also lands here now, as a placeholder
        // (role + label, no reference) rather than in Dropped.
        Assert.Equal(4, result.Portable.Count);
        Assert.Empty(result.Dropped);
        var placeholder = Assert.Single(result.Portable, entry => entry.Placeholder);
        Assert.Equal("Reference", placeholder.Role);
        Assert.Equal(string.Empty, placeholder.Reference);
    }

    [Fact]
    public void Apply_APlainAbsoluteRow_CarriesTheOriginalRoleAndLabelAsAPlaceholderInPortable()
    {
        var absoluteReference = OperatingSystem.IsWindows() ? @"C:\Users\raymond\private-notes.md" : "/home/raymond/private-notes.md";
        var result = CockpitProjectResourceFilter.Apply([("Reference", absoluteReference, "My private notes")]);

        var placeholder = Assert.Single(result.Portable);
        Assert.True(placeholder.Placeholder);
        Assert.Equal("Reference", placeholder.Role);
        Assert.Equal("My private notes", placeholder.Label);
        Assert.Equal(string.Empty, placeholder.Reference);
        Assert.Empty(result.Dropped);
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

    // AC-605: reverses the AC-244-era divergence this test used to pin — the host editor and this filter now agree that a "~/..." reference travels, so it is carried, not dropped.
    [Fact]
    public void Apply_AnchorRelativeReference_IsPortableNotDropped()
    {
        var result = CockpitProjectResourceFilter.Apply([("Reference", "~/Notes/private.md", null)]);

        var portable = Assert.Single(result.Portable);
        Assert.Equal("anchor-relative", portable.Portability);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void Apply_AbsoluteReference_IsAPlaceholderNotADropAnyMore()
    {
        // AC-246 (Raymond, 2026-08-02): reverses the AC-244-era "absolute means dropped" rule this test used to
        // pin — role and label now travel as a placeholder instead.
        var absoluteReference = OperatingSystem.IsWindows() ? @"C:\Users\raymond\private-notes.md" : "/home/raymond/private-notes.md";
        var result = CockpitProjectResourceFilter.Apply([("Reference", absoluteReference, null)]);

        var placeholder = Assert.Single(result.Portable);
        Assert.Equal("absolute", placeholder.Portability);
        Assert.True(placeholder.Placeholder);
        Assert.Empty(result.Dropped);
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

    // AC-612 (criterion 3, "Share"): a secret-shaped row is dropped through the same mechanism AC-244 already
    // built for an unportable row — no new reporting path. `Portability` still reads `AnchorRelative`
    // here (the row's own shape), distinct from why `CockpitProjectResourceEntry.Create` refused it.
    [Fact]
    public void Apply_SecretPathReference_IsDroppedAlongsideTheAbsoluteRow()
    {
        var result = CockpitProjectResourceFilter.Apply(
        [
            ("Instructions", "~/.ssh/id_rsa", "SSH key"),
            ("Instructions", "docs/CONVENTIONS.md", "Conventies"),
        ]);

        Assert.Single(result.Portable);
        var dropped = Assert.Single(result.Dropped);
        Assert.Equal("~/.ssh/id_rsa", dropped.Reference);
        Assert.Equal("SSH key", dropped.Label);
        Assert.Equal(ProjectResourcePortability.AnchorRelative, dropped.Portability);
    }
}
