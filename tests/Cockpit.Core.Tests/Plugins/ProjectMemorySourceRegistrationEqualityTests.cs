using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="ProjectMemorySourceRegistration"/>'s own equality override (AC-502/AC-499): compares
/// <c>Scheme</c>/<c>Title</c>/<c>Instruction</c>/<c>FamilyKey</c>/<c>InstanceTitle</c>, deliberately excluding
/// <c>ListLocationsAsync</c>/<c>SignInAsync</c>/<c>CheckReachability</c>. Without this, <c>DepotSettingsControl._SyncMemorySources</c>'s
/// before/after diff would treat every save as "changed" — two delegates freshly built for the very same connection
/// are never reference-equal — and force an unnecessary Remove+Add of a scheme that did not actually change.
/// <para>
/// AC-499 added <c>FamilyKey</c> and <c>InstanceTitle</c> to the compared fields — a behaviour change from AC-502's
/// three-field version: an instance rename now reads as "changed" to <c>_SyncMemorySources</c> where it did not
/// before either field existed, since it is visible content (which family's dropdown an instance appears under, and
/// what its own row in that dropdown reads), not incidental wiring like the excluded delegates.
/// </para>
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

    // --- AC-499: FamilyKey/InstanceTitle join the fields equality compares — a behaviour change from AC-502's own
    // three-field version that DepotSettingsControl._SyncMemorySources's before/after diff now depends on: moving an
    // instance to a different family, or renaming it, must read as "changed" the same as a title/instruction edit
    // already does, where it did not before either field existed. -------------------------------------------------

    [Fact]
    public void TwoRegistrations_DifferentFamilyKey_AreNotEqual()
    {
        // Moving an instance to a different family (or out of one) changes which row of the picker it appears
        // under — visible to the operator, so _SyncMemorySources must see it as a change too.
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { FamilyKey = "depot" };
        var b = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { FamilyKey = "notes" };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TwoRegistrations_OneWithAFamilyKeyAndOneWithout_AreNotEqual()
    {
        var ungrouped = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.");
        var grouped = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { FamilyKey = "depot" };

        Assert.NotEqual(ungrouped, grouped);
    }

    [Fact]
    public void TwoRegistrations_FamilyKeysDifferingOnlyInCase_AreEqual()
    {
        // FamilyKey is matched case-insensitively against ProjectMemorySourceFamily.Key, the same agreement Scheme
        // makes — a pure-case rename must not read as "changed" here either.
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { FamilyKey = "depot" };
        var b = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { FamilyKey = "Depot" };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TwoRegistrations_DifferentInstanceTitle_AreNotEqual()
    {
        // AC-499 review: this is the behaviour change from AC-502's three-field equality — a Depot connection
        // retitled ("krahwinkel-it" -> "krahwinkel-it (new)") now reads as "changed" to _SyncMemorySources where it
        // used to read as unchanged, because the instance dropdown's own row text just changed under the operator.
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { InstanceTitle = "Depot (krahwinkel-it)" };
        var b = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { InstanceTitle = "Depot (krahwinkel-it, renamed)" };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TwoRegistrations_InstanceTitlesDifferingOnlyInCase_AreNotEqual()
    {
        // Unlike FamilyKey and Scheme, InstanceTitle compares Ordinal (it is display text an operator reads, not a
        // key another consumer resolves case-insensitively) — a pure-case edit is still a visible change.
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { InstanceTitle = "Depot (krahwinkel-it)" };
        var b = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { InstanceTitle = "Depot (Krahwinkel-IT)" };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TwoRegistrations_SameFamilyKeyAndInstanceTitle_AreEqual_EvenWithDifferentConfigureDelegates()
    {
        // A pure-case FamilyKey difference must not read as "changed" while everything else, including a fresh
        // InstanceTitle-carrying shape, genuinely is unchanged — belt-and-braces alongside the single-field cases
        // above, proving the two new fields combine with the untouched three exactly the same way.
        var a = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.")
        {
            FamilyKey = "depot",
            InstanceTitle = "Depot (krahwinkel-it)",
            ListLocationsAsync = ListA,
        };
        var b = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.")
        {
            FamilyKey = "depot",
            InstanceTitle = "Depot (krahwinkel-it)",
            ListLocationsAsync = ListB,
        };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
