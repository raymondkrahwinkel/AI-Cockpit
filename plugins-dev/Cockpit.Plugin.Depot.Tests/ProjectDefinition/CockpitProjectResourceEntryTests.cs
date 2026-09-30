using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// `CockpitProjectResourceEntry.Create` — what happens to an absolute reference (AC-244). AC-605 made an
// anchor-relative reference portable; AC-246 made a plain absolute one a placeholder instead of dropped.
// A secret-shaped reference is still dropped in full, whatever its shape — AC-612, unchanged.
public class CockpitProjectResourceEntryTests
{
    [Fact]
    public void Create_AbsoluteReference_ReturnsAPlaceholderRow()
    {
        // AC-246 (Raymond, 2026-08-02): a machine-scope row is no longer an all-or-nothing drop — role and label
        // travel as a placeholder, the reference itself does not.
        var reference = OperatingSystem.IsWindows() ? @"C:\Users\raymond\Notes\private.md" : "/home/raymond/Notes/private.md";
        var entry = CockpitProjectResourceEntry.Create("Reference", reference, "Private notes");

        Assert.NotNull(entry);
        Assert.True(entry.Placeholder);
        Assert.Equal("Reference", entry.Role);
        Assert.Equal(string.Empty, entry.Reference);
        Assert.Equal("Private notes", entry.Label);
        Assert.Equal("absolute", entry.Portability);
    }

    // AC-244 (coordinator probe, 2026-08-02): a blank reference used to classify as RepoRelative and get written
    // as a portable row pointing at nothing. Create refuses it before Classify ever sees it.

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
    public void Create_AnAbsoluteSecretPathReferenceWithALabel_DropsTheLabelTooNotJustTheReference()
    {
        // AC-246: this is the row a placeholder must never become. A plain absolute path travels as role +
        // label, but a secret-shaped one must stay a full drop — a label like "Productie-DB" would leak
        // by itself even with the reference withheld. The secret check runs before the placeholder branch.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var entry = CockpitProjectResourceEntry.Create("Reference", Path.Combine(home, ".aws", "credentials"), "Productie-DB");

        Assert.Null(entry);
    }

    [Fact]
    public void Create_APublicKeyReference_IsNotTreatedAsSecret()
    {
        var entry = CockpitProjectResourceEntry.Create("Instructions", "~/.ssh/id_rsa.pub");

        Assert.NotNull(entry);
        Assert.Equal("anchor-relative", entry.Portability);
    }
}
