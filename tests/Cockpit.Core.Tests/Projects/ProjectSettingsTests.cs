using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>The project list's own rules: what survives a load, and how the manager's add/edit/remove behave.</summary>
public class ProjectSettingsTests
{
    [Fact]
    public void Normalized_DropsEntriesWithoutAnIdOrAName()
    {
        var settings = new ProjectSettings
        {
            Projects =
            [
                Project.Create("Cockpit"),
                new Project(string.Empty, "no id"),
                new Project("no-name", "  "),
            ],
        };

        Assert.Equal("Cockpit", Assert.Single(settings.Normalized().Projects).Name);
    }

    [Fact]
    public void Normalized_KeepsTheFirstOfARepeatedId()
    {
        var settings = new ProjectSettings
        {
            Projects = [new Project("same", "first"), new Project("same", "second")],
        };

        Assert.Equal("first", Assert.Single(settings.Normalized().Projects).Name);
    }

    [Fact]
    public void Normalized_DropsBlankInformationRowsAndTidiesTheRest()
    {
        var settings = new ProjectSettings
        {
            Projects =
            [
                Project.Create("Cockpit") with
                {
                    AdditionalInfo =
                    [
                        new ProjectInfoField("  Repository ", " https://github.com/example/repo "),
                        new ProjectInfoField("  ", "   "),
                    ],
                },
            ],
        };

        var info = Assert.Single(settings.Normalized().Projects).AdditionalInfo;

        Assert.Single(info);
        Assert.Equal("Repository", info[0].Label);
        Assert.Equal("https://github.com/example/repo", info[0].Value);
    }

    [Fact]
    public void Normalized_ProjectWithNothingToTidy_IsTheSameInstance()
    {
        var project = Project.Create("Cockpit") with
        {
            AdditionalInfo = [new ProjectInfoField("Repository", "https://github.com/example/repo")],
        };
        var settings = ProjectSettings.Empty.WithProject(project);

        Assert.Same(settings, settings.Normalized());
    }

    [Fact]
    public void Find_UnknownOrMissingId_ReturnsNull()
    {
        var settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit"));

        Assert.Null(settings.Find("gone"));
        Assert.Null(settings.Find(null));
    }

    [Fact]
    public void WithUpdated_SwapsTheProjectCarryingThatId()
    {
        var project = Project.Create("Cockpit");
        var settings = ProjectSettings.Empty.WithProject(project);

        var renamed = settings.WithUpdated(project with { Name = "AI-Cockpit" });

        Assert.Equal("AI-Cockpit", Assert.Single(renamed.Projects).Name);
    }

    [Fact]
    public void WithoutProject_RemovesItAndLeavesTheRest()
    {
        var kept = Project.Create("Cockpit");
        var removed = Project.Create("Depot");
        var settings = ProjectSettings.Empty.WithProject(kept).WithProject(removed);

        Assert.Equal(kept.Id, Assert.Single(settings.WithoutProject(removed.Id).Projects).Id);
    }

    // AC-245: the one per-machine "hidden shared project" flag — never in the shared definition, always local.

    [Fact]
    public void Normalized_TrimsDedupesAndDropsBlankHiddenSharedProjectIds()
    {
        var settings = new ProjectSettings
        {
            HiddenSharedProjectIds = ["  depot:cockpit  ", "depot:cockpit", "", "   ", "depot:other"],
        };

        Assert.Equal(["depot:cockpit", "depot:other"], settings.Normalized().HiddenSharedProjectIds);
    }

    [Fact]
    public void Normalized_HandlesANullEntryInHiddenSharedProjectIds()
    {
        // A hand-edited cockpit.json can hold a JSON null in this array; it must cost that one entry, not the load.
        var settings = new ProjectSettings { HiddenSharedProjectIds = [null!, "depot:cockpit"] };

        Assert.Equal(["depot:cockpit"], settings.Normalized().HiddenSharedProjectIds);
    }

    [Fact]
    public void Normalized_WithNothingToTidyInHiddenSharedProjectIds_IsTheSameInstance()
    {
        var settings = ProjectSettings.Empty with { HiddenSharedProjectIds = ["depot:cockpit"] };

        Assert.Same(settings, settings.Normalized());
    }

    [Fact]
    public void IsSharedProjectHidden_TrueOnlyForAnIdInTheList()
    {
        var settings = ProjectSettings.Empty with { HiddenSharedProjectIds = ["depot:cockpit"] };

        Assert.True(settings.IsSharedProjectHidden("depot:cockpit"));
        Assert.False(settings.IsSharedProjectHidden("depot:other"));
    }

    // AC-618: categories — local ordering/casing ledger, never alphabetical, disappears with its last project.

    [Fact]
    public void Normalized_NoProjectHasACategory_CategoryOrderStaysEmpty()
    {
        var settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit"));

        Assert.Empty(settings.Normalized().CategoryOrder);
    }

    [Fact]
    public void Normalized_AppendsANewlyUsedCategory_InTheOrderItsFirstProjectAppears()
    {
        var settings = ProjectSettings.Empty
            .WithProject(Project.Create("Cockpit") with { Category = "Werk" })
            .WithProject(Project.Create("Home lab") with { Category = "Privé" });

        Assert.Equal(["Werk", "Privé"], settings.Normalized().CategoryOrder);
    }

    [Fact]
    public void Normalized_PreservesAnExplicitCategoryOrderNotAlphabetical()
    {
        var settings = new ProjectSettings
        {
            CategoryOrder = ["Privé", "Werk"],
            Projects =
            [
                Project.Create("Cockpit") with { Category = "Werk" },
                Project.Create("Home lab") with { Category = "Privé" },
            ],
        };

        // "Privé" sorts after "Werk" alphabetically — this proves the order is not being re-derived that way.
        Assert.Equal(["Privé", "Werk"], settings.Normalized().CategoryOrder);
    }

    [Fact]
    public void Normalized_DropsACategoryFromTheOrder_OnceItsLastProjectLetsGoOfIt()
    {
        var settings = new ProjectSettings
        {
            CategoryOrder = ["Werk", "Privé"],
            Projects = [Project.Create("Cockpit") with { Category = "Privé" }],
        };

        Assert.Equal(["Privé"], settings.Normalized().CategoryOrder);
    }

    [Fact]
    public void Normalized_KeepsTheOrderEntrysCasing_NotThePossiblyDifferentlyCasedProjectCategory()
    {
        // The project itself typed "werk" (lower-case) but the order already remembers "Werk" as the first-typed
        // casing — the heading must keep showing "Werk", the AC-618 "shown as first typed" rule, matched
        // case-insensitively (AC-372's own lesson: never StringComparison.CurrentCultureIgnoreCase for this).
        var settings = new ProjectSettings
        {
            CategoryOrder = ["Werk"],
            Projects = [Project.Create("Cockpit") with { Category = "werk" }],
        };

        Assert.Equal(["Werk"], settings.Normalized().CategoryOrder);
    }

    [Fact]
    public void Normalized_MergesCategoriesThatDifferOnlyInCasing_KeepingOneEntry()
    {
        var settings = ProjectSettings.Empty
            .WithProject(Project.Create("A") with { Category = "werk" })
            .WithProject(Project.Create("B") with { Category = "Werk" })
            .WithProject(Project.Create("C") with { Category = "WERK" });

        Assert.Equal(["werk"], settings.Normalized().CategoryOrder);
    }

    [Fact]
    public void Normalized_WhitespaceOnlyCategory_IsTreatedAsNoCategory()
    {
        var settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit") with { Category = "   " });

        var normalized = settings.Normalized();

        Assert.Null(Assert.Single(normalized.Projects).Category);
        Assert.Empty(normalized.CategoryOrder);
    }

    [Fact]
    public void Normalized_TrimsALeadingOrTrailingSpaceOffACategory()
    {
        var settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit") with { Category = "  Werk  " });

        Assert.Equal("Werk", Assert.Single(settings.Normalized().Projects).Category);
    }

    [Fact]
    public void Normalized_ALongOrUnicodeCategory_RoundTripsUntouched()
    {
        // Built to exactly 200 characters rather than hand-counted, RTL Hebrew up front (Iron Law #4-adjacent: a
        // test that must not itself be the miscounted thing).
        const string prefix = "מחלקת עבודה — ";
        var longName = prefix + new string('x', 200 - prefix.Length);
        Assert.Equal(200, longName.Length);

        var settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit") with { Category = longName });

        var normalized = settings.Normalized();
        Assert.Equal(longName, Assert.Single(normalized.Projects).Category);
        Assert.Equal([longName], normalized.CategoryOrder);
    }

    [Fact]
    public void Normalized_WithNothingToTidyInCategoryOrder_IsTheSameInstance()
    {
        var settings = ProjectSettings.Empty with
        {
            CategoryOrder = ["Werk"],
            Projects = [Project.Create("Cockpit") with { Category = "Werk" }],
        };

        Assert.Same(settings, settings.Normalized());
    }
}
