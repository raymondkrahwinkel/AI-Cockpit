using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectResourceFilterTests
{

    // AC-605: reverses the AC-244-era divergence this test used to pin — the host editor and this filter now agree that a "~/..." reference travels, so it is carried, not dropped.

    // AC-244 (coordinator probe, 2026-08-02): a blank reference is a different reason than an unportable shape —
    // Portability is null here rather than the misleading RepoRelative Classify would report for the bare text.

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
