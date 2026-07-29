using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The resource section's own row mechanics (AC-485): adding and removing a row, a blank one costing nothing on
/// save, a project with none behaving exactly as before it existed, and the two diagnostics the editor now runs for
/// itself — a reference the probe cannot find, and one that names a place only this machine has. Covers the five
/// acceptance criteria together with <see cref="ProjectDialogViewModelTests"/> (round trips, editing one row) and
/// <see cref="ProjectResourcePathPortabilityTests"/> (what a picked path is actually stored as).
/// </summary>
public class ProjectDialogResourceRowTests
{
    private static ISessionProfileStore ProfileStore()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);
        return store;
    }

    private static IMcpServerCatalog Catalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns([]);
        return catalog;
    }

    // Rooted per platform (this repo's CI runs on Linux, this box runs Windows) and unique per call, so the path
    // is guaranteed absent without depending on anything about the machine running the test beyond its temp folder
    // existing at all.
    private static string _NonExistentAbsolutePath() =>
        Path.Combine(Path.GetTempPath(), "cockpit-tests-missing-" + Guid.NewGuid().ToString("N") + ".md");

    private static string _Root(params string[] segments) =>
        Path.Combine([OperatingSystem.IsWindows() ? @"C:\Users\raymond\Cockpit" : "/home/raymond/Cockpit", .. segments]);

    // --- AC: add/remove a row, a blank one is not saved -------------------------------------------------------------

    [Fact]
    public async Task AddAndRemoveResourceRow_AppendAnEmptyRowAndTakeItBack()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());

        viewModel.AddResourceRowCommand.Execute(null);
        var row = Assert.Single(viewModel.ResourceRows);
        Assert.Empty(row.Reference);

        viewModel.RemoveResourceRowCommand.Execute(viewModel.ResourceRows[0]);
        Assert.Empty(viewModel.ResourceRows);
    }

    /// <summary>
    /// AC-485 review (FIX 8): only the last row's own bottom divider drops out in the view — see
    /// <see cref="ProjectResourceRowViewModel.IsLastRow"/>'s own remarks on why the dialog sets this explicitly
    /// rather than leaving the view to work it out from <see cref="ProjectDialogViewModel.ResourceRows"/> itself.
    /// </summary>
    [Fact]
    public async Task AddingAndRemovingRows_KeepsIsLastRowOnExactlyTheLastOne()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());

        viewModel.AddResourceRowCommand.Execute(null);
        Assert.True(viewModel.ResourceRows.Single().IsLastRow, "the only row is trivially the last one");

        viewModel.AddResourceRowCommand.Execute(null);
        Assert.False(viewModel.ResourceRows[0].IsLastRow, "a second row was just added after it");
        Assert.True(viewModel.ResourceRows[1].IsLastRow);

        viewModel.RemoveResourceRowCommand.Execute(viewModel.ResourceRows[1]);
        Assert.True(viewModel.ResourceRows.Single().IsLastRow, "removing the only row after it makes this one last again");
    }

    [Fact]
    public async Task ToProject_ABlankResourceRow_YieldsNoRow()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);

        // A row the operator added and left alone names nothing.
        Assert.Empty(viewModel.ToProject().Resources);
    }

    [Fact]
    public async Task ToProject_ARowWithOnlyALabelAndABlankReference_YieldsNoRow()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows[0].Label = "Handbook";

        // A label with no reference names nothing a session could go read.
        Assert.Empty(viewModel.ToProject().Resources);
    }

    // --- AC: a project with no resources behaves exactly as before (regression) --------------------------------------

    [Fact]
    public async Task ToProject_NoResources_ProducesAnEmptyResourcesListAndNullMemoryRef()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";

        var saved = viewModel.ToProject();

        Assert.Empty(saved.Resources);
        Assert.Null(saved.MemoryRef);
    }

    [Fact]
    public async Task CreateAsync_AProjectWithNoResources_OpensWithNoRows()
    {
        var project = Project.Create("Cockpit");

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        // The resource section starts exactly as empty as the old dialog's Memory box did when nothing was set.
        Assert.Empty(viewModel.ResourceRows);
    }

    // --- AC: three roles round-trip unchanged, editing one row touches only that row -----------------------------------

    [Fact]
    public async Task RoundTrip_AProjectWithAllThreeRoles_SurvivesOpenAndSaveUnchanged()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("/home/raymond/Notes/Cockpit", ProjectResourceRole.Memory) { Label = "Team notes" },
                new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook" },
                new ProjectResource(_Root("handbook"), ProjectResourceRole.Reference) { Label = "Old handbook", ReachesSessions = false },
            ],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.Equal(project.Resources, viewModel.ToProject().Resources);
    }

    /// <summary>
    /// AC-485 review (FIX 9): the test above passes no registered memory source, so <c>MemorySourceChoices</c> is
    /// empty and <see cref="ProjectResourceRowViewModel.ShowsMemorySourcePicker"/> is false for every row — the one
    /// branch in <see cref="ProjectResourceRowViewModel.ToDomain"/> that folds a picked scheme back into the saved
    /// reference (the only place this round trip could actually lose something) never runs. This variant registers
    /// "depot" and gives the Memory row a reference that names it, so the fold/unfold path is the one actually
    /// exercised here.
    /// </summary>
    [Fact]
    public async Task RoundTrip_AProjectWithAllThreeRolesAndARegisteredMemorySource_SurvivesOpenAndSaveUnchanged()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("depot:cockpit", ProjectResourceRole.Memory) { Label = "Team notes" },
                new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook" },
                new ProjectResource(_Root("handbook"), ProjectResourceRole.Reference) { Label = "Old handbook", ReachesSessions = false },
            ],
        };
        var depotSource = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it through the Depot MCP.");

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);

        // Both checked, the same as ProjectDialogMemorySourceTests.RoundTrip_ADepotReference_SurvivesLoadAndSaveUnchanged:
        // not merely "the saved string happens to match" but "the picker actually selected the Depot source".
        var memoryRow = viewModel.ResourceRows.Single(row => row.Role == ProjectResourceRole.Memory);
        Assert.Equal("depot", memoryRow.SelectedMemorySourceChoice?.Scheme);
        Assert.Equal("cockpit", memoryRow.Reference);

        Assert.Equal(project.Resources, viewModel.ToProject().Resources);
    }

    [Fact]
    public async Task ToProject_EditingOneRowsLabel_LeavesTheOthersReferenceAndRoleAlone()
    {
        var memory = new ProjectResource("/home/raymond/Notes/Cockpit", ProjectResourceRole.Memory);
        var instructions = new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook" };
        var reference = new ProjectResource(_Root("handbook"), ProjectResourceRole.Reference) { Label = "Old handbook" };
        var project = Project.Create("Cockpit") with { Resources = [memory, instructions, reference] };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());
        viewModel.ResourceRows.Single(row => row.Role == ProjectResourceRole.Instructions).Label = "House conventions";

        var saved = viewModel.ToProject();

        // AC-485 review (FIX 9): order matters for a list the operator arranges themselves — comparing the whole
        // sequence pins the whole order, not merely that memory and reference each still appear somewhere in it.
        Assert.Equal(new[] { memory, instructions with { Label = "House conventions" }, reference }, saved.Resources);
    }

    // --- AC: a broken reference is visible in the editor itself, not only in a prompt -----------------------------------

    [Fact]
    public async Task CreateAsync_AReferenceThatDoesNotExist_MarksItsRowBroken()
    {
        var missing = _NonExistentAbsolutePath();
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource(missing, ProjectResourceRole.Reference)],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.True(viewModel.ResourceRows.Single().IsBroken);
    }

    [Fact]
    public async Task CreateAsync_AReferenceThatExists_LeavesItsRowUnbroken()
    {
        var project = Project.Create("Cockpit") with
        {
            // The temp folder itself: guaranteed to exist on whatever machine runs this test, whichever OS it is.
            Resources = [new ProjectResource(Path.GetTempPath(), ProjectResourceRole.Reference)],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.False(viewModel.ResourceRows.Single().IsBroken);
    }

    /// <summary>
    /// AC-485 review (FIX 3): the old version of this test used a single row, which cannot tell "never judged"
    /// apart from "judged and found present" — both read as <c>IsBroken == false</c>. Two rows sharing the very same
    /// missing path can: row A still reaches sessions, so the probe judges it and finds it missing; row B does not,
    /// so <see cref="Infrastructure.Projects.ProjectResourceProbe.FindUnresolved"/> never even sees it — filtered
    /// out before any I/O runs, per that method's own rules. The bug this pins matched a row's <c>IsBroken</c>
    /// against the probe's result by comparing bare reference <em>text</em>, so row B was wrongly marked broken too,
    /// purely because its text happened to match row A's.
    /// </summary>
    [Fact]
    public async Task CreateAsync_TwoRowsShareAMissingPath_OnlyTheOneStillReachingSessionsIsMarkedBroken()
    {
        var missing = _NonExistentAbsolutePath();
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource(missing, ProjectResourceRole.Reference),
                new ProjectResource(missing, ProjectResourceRole.Reference) { ReachesSessions = false },
            ],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.True(viewModel.ResourceRows[0].IsBroken, "this row reaches sessions and the probe found its path missing");
        Assert.False(
            viewModel.ResourceRows[1].IsBroken,
            "this row never reaches sessions, so the probe never judged it — even though its text matches the row the probe did find missing");
    }

    /// <summary>
    /// AC-485 review (MUST-FIX 2): the probe's I/O now runs off the UI thread (see
    /// <c>ProjectDialogViewModel._RunResourceDiagnosticsAsync</c>), so a Reference edit no longer marks a row broken
    /// the instant the property is set — <see cref="ProjectDialogViewModel.ResourceDiagnosticsRefreshCompleted"/> is
    /// this test's hook to wait for the background answer instead of a real sleep. Also pins FIX 4: an
    /// <see cref="ProjectResourceRowViewModel"/> flips exactly once, once the check has actually run — not for
    /// every intermediate value a keystroke might produce, which this single assignment already stands in for
    /// (the view itself never even forwards an intermediate keystroke — see the Reference box's
    /// <c>UpdateSourceTrigger=LostFocus</c> binding).
    /// </summary>
    [Fact]
    public async Task TypingAReference_ThatDoesNotExist_MarksTheRowBrokenOnceChecked()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.Role = ProjectResourceRole.Reference;

        row.Reference = _NonExistentAbsolutePath();
        await viewModel.ResourceDiagnosticsRefreshCompleted;

        Assert.True(row.IsBroken, "the row is marked broken once the background check answers, the same as opening a project that already had one");
    }

    /// <summary>
    /// AC-485 review (FIX 7): <c>Path.GetFullPath</c> throws for a reference containing a NUL character — reachable
    /// from a hand-edited <c>cockpit.json</c>, not only from the picker. Before this fix that exception came
    /// straight out of <c>CreateAsync</c> (via <c>ProjectResourcePathPortability.IsMachineBound</c>, called from the
    /// diagnostics refresh <c>CreateAsync</c> awaits), so the dialog never opened at all over one bad row.
    /// </summary>
    [Fact]
    public async Task CreateAsync_AResourceWithAnIllegalCharacterInItsReference_StillOpens()
    {
        var project = Project.Create("Cockpit") with
        {
            // SourceDirectory must actually be set: IsMachineBound returns early (no Path.GetFullPath call at all)
            // when it is blank, which would leave this test green even without the guard it means to pin.
            SourceDirectory = _Root(),
            Resources = [new ProjectResource(_Root("bad\0name.md"), ProjectResourceRole.Reference)],
        };

        var exception = await Record.ExceptionAsync(() => ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog()));

        // A hand-edited cockpit.json with one bad row must cost that row, not the dialog itself.
        Assert.Null(exception);
    }

    // --- AC: a role switch cannot silently change what Reference means (AC-485 review, MUST-FIX 1) -----------------

    [Fact]
    public async Task SwitchingARowsRoleAwayFromMemory_WithADepotSourcePicked_FoldsTheSchemeIntoTheVisibleBox()
    {
        var depotSource = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it through the Depot MCP.");
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:cockpit" };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);
        var row = viewModel.ResourceRows.Single();
        // The row must actually have the Depot source selected before this test means anything.
        Assert.Equal("depot", row.SelectedMemorySourceChoice!.Scheme);

        row.Role = ProjectResourceRole.Reference;

        // The picker is about to disappear, so what it folded away must land in the box the operator can still see.
        Assert.Equal("depot:cockpit", row.Reference);
        // The picker's own selection must not keep pointing at a source the box no longer names.
        Assert.Equal(viewModel.MemorySourceChoices[0], row.SelectedMemorySourceChoice);
        // Saving now must not silently write a bare value that resolves to nothing.
        Assert.Equal("depot:cockpit", viewModel.ToProject().Resources.Single().Reference);
    }

    [Fact]
    public async Task SwitchingARowsRoleAwayFromMemory_WithFolderPicked_LeavesTheReferenceAlone()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.Reference = "/home/raymond/Notes/Cockpit";

        row.Role = ProjectResourceRole.Reference;

        // A Folder-mode row never had a scheme hidden behind it, so switching away from Memory has nothing to fold.
        Assert.Equal("/home/raymond/Notes/Cockpit", row.Reference);
    }

    [Fact]
    public async Task SwitchingARowsRoleToMemory_WithASchemeReferenceAlreadyTyped_SelectsThatSourceAndShowsTheBareValue()
    {
        var depotSource = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it through the Depot MCP.");
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog(), memorySources: [depotSource]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.Role = ProjectResourceRole.Reference;
        row.Reference = "depot:cockpit";

        row.Role = ProjectResourceRole.Memory;

        // Switching a row onto Memory must select the source its typed reference already names, the same as
        // loading one from disk.
        Assert.Equal("depot", row.SelectedMemorySourceChoice?.Scheme);
        // The box shows what the plugin queries with, not the scheme prefix, mirroring CreateAsync's own load-time unfold.
        Assert.Equal("cockpit", row.Reference);
    }

    [Fact]
    public async Task SwitchingARowsRoleToMemory_WithAPlainPath_SelectsFolderAndLeavesTheReferenceAlone()
    {
        var depotSource = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it through the Depot MCP.");
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog(), memorySources: [depotSource]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.Role = ProjectResourceRole.Reference;
        row.Reference = "/home/raymond/Notes/Cockpit";

        row.Role = ProjectResourceRole.Memory;

        // A plain path names no registered source, so Folder is selected — the same fallback CreateAsync applies.
        Assert.Equal(viewModel.MemorySourceChoices[0], row.SelectedMemorySourceChoice);
        Assert.Equal("/home/raymond/Notes/Cockpit", row.Reference);
    }

    // --- AC: a machine-bound reference is visible in the editor -----------------------------------------------------------

    [Fact]
    public async Task CreateAsync_AnAbsoluteReferenceOutsideTheProjectFolder_MarksItsRowMachineBound()
    {
        var project = Project.Create("Cockpit") with
        {
            SourceDirectory = _Root(),
            Resources = [new ProjectResource(_Root("..", "Elsewhere", "handbook.md"), ProjectResourceRole.Reference)],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.True(viewModel.ResourceRows.Single().IsMachineBound);
    }

    [Fact]
    public async Task CreateAsync_AnAbsoluteReferenceInsideTheProjectFolder_IsNeverMachineBound()
    {
        var project = Project.Create("Cockpit") with
        {
            SourceDirectory = _Root(),
            Resources = [new ProjectResource(_Root("docs", "handbook.md"), ProjectResourceRole.Reference)],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.False(viewModel.ResourceRows.Single().IsMachineBound);
    }

    [Fact]
    public async Task CreateAsync_ARelativeReference_IsNeverMachineBound()
    {
        var project = Project.Create("Cockpit") with
        {
            SourceDirectory = _Root(),
            Resources = [new ProjectResource(Path.Combine("docs", "handbook.md"), ProjectResourceRole.Reference)],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.False(viewModel.ResourceRows.Single().IsMachineBound);
    }

    [Fact]
    public async Task ChangingSourceDirectory_ReEvaluatesWhetherAResourceRowIsMachineBound()
    {
        var reference = _Root("docs", "handbook.md");
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows[0].Role = ProjectResourceRole.Reference;
        viewModel.ResourceRows[0].Reference = reference;
        await viewModel.ResourceDiagnosticsRefreshCompleted;

        Assert.True(viewModel.ResourceRows[0].IsMachineBound, "no folder is set yet, so any absolute path is machine-bound");

        viewModel.SourceDirectory = _Root();
        await viewModel.ResourceDiagnosticsRefreshCompleted;

        Assert.False(viewModel.ResourceRows[0].IsMachineBound, "the folder now contains the reference");
    }

    // --- AC-486: "Send along" is Instructions-only, off by default, round-trips, and cannot survive a role switch ----

    [Fact]
    public async Task AddResourceRow_DefaultsSendsContentToFalse()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.AddResourceRowCommand.Execute(null);

        // Opening and reading a file is an opt-in, not a default.
        Assert.False(viewModel.ResourceRows.Single().SendsContent);
    }

    [Fact]
    public async Task RoundTrip_AnInstructionsRowWithSendsContentTicked_SurvivesOpenAndSaveUnchanged()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook", SendsContent = true }],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        // CreateAsync must read the stored opt-in back onto the row.
        Assert.True(viewModel.ResourceRows.Single().SendsContent);
        Assert.Equal(project.Resources, viewModel.ToProject().Resources);
    }

    [Fact]
    public async Task RoundTrip_AnInstructionsRowWithSendsContentLeftOff_SurvivesOpenAndSaveUnchanged()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook" }],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.False(viewModel.ResourceRows.Single().SendsContent);
        Assert.Equal(project.Resources, viewModel.ToProject().Resources);
    }

    [Theory]
    [InlineData(ProjectResourceRole.Memory)]
    [InlineData(ProjectResourceRole.Reference)]
    public async Task ShowsSendsContentOption_IsFalseForRolesOtherThanInstructions(ProjectResourceRole role)
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];

        row.Role = role;

        Assert.False(row.ShowsSendsContentOption);
    }

    [Fact]
    public async Task ShowsSendsContentOption_IsTrueForInstructions()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];

        row.Role = ProjectResourceRole.Instructions;

        Assert.True(row.ShowsSendsContentOption);
    }

    [Fact]
    public async Task SwitchingARowsRoleAwayFromInstructions_TurnsSendsContentBackOff()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { SendsContent = true }],
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());
        var row = viewModel.ResourceRows.Single();
        Assert.True(row.SendsContent, "must actually start ticked before this test means anything");

        row.Role = ProjectResourceRole.Reference;

        Assert.False(
            row.SendsContent,
            "the checkbox is about to disappear for this role, so it must not stay silently ticked on a row where it now means nothing");
        Assert.False(
            viewModel.ToProject().Resources.Single().SendsContent,
            "saving now must not carry an opt-in to open a file the operator can no longer even see a box for");
    }

    [Fact]
    public async Task SwitchingARowOntoInstructions_LeavesSendsContentOff()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.Role = ProjectResourceRole.Reference;

        row.Role = ProjectResourceRole.Instructions;

        // Switching a row onto Instructions must not silently opt it into having its file read and sent.
        Assert.False(row.SendsContent);
    }
}
