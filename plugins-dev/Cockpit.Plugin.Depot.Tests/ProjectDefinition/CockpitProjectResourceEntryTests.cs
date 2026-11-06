using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// `CockpitProjectResourceEntry.Create` — what happens to an absolute reference (AC-244). AC-605 made an
// anchor-relative reference portable; AC-246 made a plain absolute one a placeholder instead of dropped.
// A secret-shaped reference is still dropped in full, whatever its shape — AC-612, unchanged.
public class CockpitProjectResourceEntryTests
{
    // --- AC-612: a reference ProjectResourceSecretPathHeuristic recognises is refused outright, whatever shape it is otherwise ---

    [Fact]
    public void Create_AnAbsoluteSecretPathReferenceWithALabel_DropsTheLabelTooNotJustTheReference()
    {
        // AC-246: this is the row a placeholder must never become. A plain absolute path travels as role +
        // label, but a secret-shaped one must stay a full drop — a label like "Productie-DB" would leak
        // by itself even with the reference withheld. The secret check runs before the placeholder branch.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var entry = CockpitProjectResourceEntry.Create("Reference", Path.Combine(home, ".aws", "credentials"), "Productie-DB");

        Assert.Null(entry);
    }
}
