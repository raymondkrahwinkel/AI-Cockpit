using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="ProjectMemorySourceRegistration"/>'s own equality override (AC-502): compares only
/// <c>Scheme</c>/<c>Title</c>/<c>Instruction</c>, deliberately excluding <c>ListLocationsAsync</c>/<c>SignInAsync</c>.
/// Without this, <c>DepotSettingsControl._SyncMemorySources</c>'s before/after diff would treat every save as
/// "changed" — two delegates freshly built for the very same connection are never reference-equal — and force an
/// unnecessary Remove+Add of a scheme that did not actually change.
/// </summary>
public class ProjectMemorySourceRegistrationEqualityTests
{
    private static Task<ProjectMemorySourceLocationsResult> ListA(CancellationToken _) =>
        Task.FromResult(ProjectMemorySourceLocationsResult.Success([]));

    private static Task<ProjectMemorySourceLocationsResult> ListB(CancellationToken _) =>
        Task.FromResult(ProjectMemorySourceLocationsResult.Success([]));

    [Fact]
    public void TwoRegistrations_SameFieldsButDifferentDelegateInstances_AreEqual()
    {
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { ListLocationsAsync = ListA };
        var b = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { ListLocationsAsync = ListB };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TwoRegistrations_DifferentScheme_AreNotEqual()
    {
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.");
        var b = new ProjectMemorySourceRegistration("depot.work", "Depot project", "Read it there.");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TwoRegistrations_DifferentTitle_AreNotEqual()
    {
        var a = new ProjectMemorySourceRegistration("depot", "Depot project — Alpha", "Read it there.");
        var b = new ProjectMemorySourceRegistration("depot", "Depot project — Beta", "Read it there.");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TwoRegistrations_DifferentInstruction_AreNotEqual()
    {
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it through Alpha.");
        var b = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it through Beta.");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TwoRegistrations_SchemesDifferingOnlyInCase_AreEqual()
    {
        // ProjectMemorySourceRegistry and a project's stored MemoryRef both match a scheme case-insensitively
        // (see Scheme's own doc comment) — a case-sensitive comparison here would let a pure-case rename read as
        // "changed" to DepotSettingsControl._SyncMemorySources while every other consumer treats it as unchanged.
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.");
        var b = new ProjectMemorySourceRegistration("Depot", "Depot project", "Read it there.");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void OneWithADelegateAndOneWithout_SameFieldsOtherwise_AreStillEqual()
    {
        // A source that starts offering listing (a Depot connection signing in for the first time, say) must not
        // read as "changed" to a caller that only cares about the three fields a session's own instructions come from.
        var withoutListing = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.");
        var withListing = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { ListLocationsAsync = ListA };

        Assert.Equal(withoutListing, withListing);
    }
}
