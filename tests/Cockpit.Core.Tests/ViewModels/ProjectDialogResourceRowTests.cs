using System.Collections.ObjectModel;
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
    /// straight out of <c>CreateAsync</c> (via <c>ProjectResourcePathPortability.ClassifyScope</c>/<c>SuggestRepoRelativeFix</c>,
    /// called from the diagnostics refresh <c>CreateAsync</c> awaits), so the dialog never opened at all over one bad row.
    /// </summary>
    [Fact]
    public async Task CreateAsync_AResourceWithAnIllegalCharacterInItsReference_StillOpens()
    {
        var project = Project.Create("Cockpit") with
        {
            // SourceDirectory must actually be set: SuggestRepoRelativeFix returns early (no Path.GetFullPath call
            // at all) when it is blank, which would leave this test green even without the guard it means to pin.
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

    // --- AC-605: a resource row's scope is visible in the editor, and an in-folder absolute path gets a fix ------

    [Fact]
    public async Task CreateAsync_AnAbsoluteReferenceOutsideTheProjectFolder_MarksItsRowMachineScoped()
    {
        var project = Project.Create("Cockpit") with
        {
            SourceDirectory = _Root(),
            Resources = [new ProjectResource(_Root("..", "Elsewhere", "handbook.md"), ProjectResourceRole.Reference)],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        var row = viewModel.ResourceRows.Single();
        Assert.Equal(ProjectResourceScope.Machine, row.Scope);
        // Outside the folder entirely, so there is nothing to offer converting it to.
        Assert.Null(row.RepoRelativeFix);
    }

    /// <summary>
    /// AC-605 criterion 5: an absolute reference that never went through the picker's own <c>ToStoredReference</c>
    /// conversion (hand-typed, or a hand-edited <c>cockpit.json</c>) is still reported <see cref="ProjectResourceScope.Machine"/>
    /// — it genuinely is one, as stored — but this is exactly the case <see cref="ProjectResourceRowViewModel.RepoRelativeFix"/>
    /// exists for: recognised, with a fix offered, rather than the old <c>IsMachineBound</c> silently reading false
    /// and hiding the row's own absolute-path shape from the operator entirely.
    /// </summary>
    [Fact]
    public async Task CreateAsync_AnAbsoluteReferenceInsideTheProjectFolder_OffersARepoRelativeFix()
    {
        var project = Project.Create("Cockpit") with
        {
            SourceDirectory = _Root(),
            Resources = [new ProjectResource(_Root("docs", "handbook.md"), ProjectResourceRole.Reference)],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        var row = viewModel.ResourceRows.Single();
        Assert.Equal(ProjectResourceScope.Machine, row.Scope);
        Assert.Equal("docs/handbook.md", row.RepoRelativeFix);
    }

    [Fact]
    public async Task CreateAsync_ARelativeReference_IsRepoScopedWithNoFixOffered()
    {
        var project = Project.Create("Cockpit") with
        {
            SourceDirectory = _Root(),
            Resources = [new ProjectResource(Path.Combine("docs", "handbook.md"), ProjectResourceRole.Reference)],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        var row = viewModel.ResourceRows.Single();
        Assert.Equal(ProjectResourceScope.Repo, row.Scope);
        Assert.Null(row.RepoRelativeFix);
    }

    [Fact]
    public async Task ChangingSourceDirectory_ReEvaluatesWhetherAResourceRowHasARepoRelativeFixAvailable()
    {
        var reference = _Root("docs", "handbook.md");
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows[0].Role = ProjectResourceRole.Reference;
        viewModel.ResourceRows[0].Reference = reference;
        await viewModel.ResourceDiagnosticsRefreshCompleted;

        // No folder is set yet, so there is nothing to offer converting it relative to.
        Assert.Equal(ProjectResourceScope.Machine, viewModel.ResourceRows[0].Scope);
        Assert.Null(viewModel.ResourceRows[0].RepoRelativeFix);

        viewModel.SourceDirectory = _Root();
        await viewModel.ResourceDiagnosticsRefreshCompleted;

        // Still an absolute path, as stored — the folder now contains the reference, so a fix is offered.
        Assert.Equal(ProjectResourceScope.Machine, viewModel.ResourceRows[0].Scope);
        Assert.Equal("docs/handbook.md", viewModel.ResourceRows[0].RepoRelativeFix);
    }

    /// <summary>AC-605 criterion 5: applying the offered fix rewrites Reference in place, the one path that ever does so without the operator picking the value from the folder browser.</summary>
    [Fact]
    public async Task ApplyingTheRepoRelativeFix_RewritesTheReferenceInPlace()
    {
        var reference = _Root("docs", "handbook.md");
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.SourceDirectory = _Root();
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows[0].Role = ProjectResourceRole.Reference;
        viewModel.ResourceRows[0].Reference = reference;
        await viewModel.ResourceDiagnosticsRefreshCompleted;
        Assert.Equal("docs/handbook.md", viewModel.ResourceRows[0].RepoRelativeFix);

        viewModel.ResourceRows[0].ApplyRepoRelativeFixCommand.Execute(null);

        Assert.Equal("docs/handbook.md", viewModel.ResourceRows[0].Reference);
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

    // --- AC-503: a Memory row's own reachability check ---------------------------------------------------------

    private static ProjectMemorySourceRegistration _DepotSourceWithCheck(
        Func<string, CancellationToken, Task<ProjectMemorySourceReachabilityResult>> check) =>
        new("depot", "Depot project", "Read it through the Depot MCP.") { CheckReachability = check };

    [Fact]
    public async Task RegressionTest_ASourceWithNoCheckDelegate_LeavesTheRowExactlyAsBeforeAC503()
    {
        // A registration a plugin built before AC-503 existed (or one whose author simply never implemented a
        // check) carries CheckReachability = null — this proves such a row shows none of the four states, the
        // same as today.
        var depotSource = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it through the Depot MCP.");
        var project = Project.Create("Cockpit") with { Resources = [new ProjectResource("depot:cockpit", ProjectResourceRole.Memory)] };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);
        var row = viewModel.ResourceRows.Single();

        Assert.Null(row.Reachability);
        Assert.False(row.IsConfirmedReachable);
        Assert.False(row.IsNotFoundReachable);
        Assert.False(row.IsNotSignedIn);
        Assert.False(row.IsCheckFailed);
    }

    [Fact]
    public async Task EmptyField_ShowsNoneOfTheFourStates_AndNeverCallsTheCheckAtAll()
    {
        var calls = 0;
        var depotSource = _DepotSourceWithCheck((_, _) =>
        {
            calls++;
            return Task.FromResult(ProjectMemorySourceReachabilityResult.Confirmed(null));
        });
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog(), memorySources: [depotSource]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];

        // Reference is left blank — AC-503 acceptance criterion 6.
        await viewModel.ResourceDiagnosticsRefreshCompleted;

        Assert.Equal(0, calls);
        Assert.Null(row.Reachability);
        Assert.False(row.IsConfirmedReachable);
        Assert.False(row.IsNotFoundReachable);
        Assert.False(row.IsNotSignedIn);
        Assert.False(row.IsCheckFailed);
    }

    [Fact]
    public async Task AConfirmedValue_SetsReachabilityAndShowsTheDetail()
    {
        var depotSource = _DepotSourceWithCheck((value, _) =>
            Task.FromResult(ProjectMemorySourceReachabilityResult.Confirmed($"24 documents for {value}")));
        var project = Project.Create("Cockpit") with { Resources = [new ProjectResource("depot:cockpit", ProjectResourceRole.Memory)] };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);
        var row = viewModel.ResourceRows.Single();

        Assert.Equal(ProjectMemorySourceReachability.Confirmed, row.Reachability);
        Assert.True(row.IsConfirmedReachable);
        Assert.False(row.IsNotFoundReachable);
        Assert.False(row.IsNotSignedIn);
        Assert.False(row.IsCheckFailed);
        Assert.Equal("24 documents for cockpit", row.ReachabilityDetail);
    }

    [Fact]
    public async Task ANotFoundValue_SetsReachabilityNotFound()
    {
        var depotSource = _DepotSourceWithCheck((_, _) => Task.FromResult(ProjectMemorySourceReachabilityResult.NotFound));
        var project = Project.Create("Cockpit") with { Resources = [new ProjectResource("depot:no-such-project", ProjectResourceRole.Memory)] };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);
        var row = viewModel.ResourceRows.Single();

        Assert.Equal(ProjectMemorySourceReachability.NotFound, row.Reachability);
        Assert.True(row.IsNotFoundReachable);
        Assert.False(row.IsConfirmedReachable);
        Assert.False(row.IsNotSignedIn);
        Assert.False(row.IsCheckFailed);
    }

    [Fact]
    public async Task ANotSignedInValue_SetsReachabilityNotSignedIn()
    {
        var depotSource = _DepotSourceWithCheck((_, _) => Task.FromResult(ProjectMemorySourceReachabilityResult.NotSignedIn));
        var project = Project.Create("Cockpit") with { Resources = [new ProjectResource("depot:cockpit", ProjectResourceRole.Memory)] };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);
        var row = viewModel.ResourceRows.Single();

        Assert.Equal(ProjectMemorySourceReachability.NotSignedIn, row.Reachability);
        Assert.True(row.IsNotSignedIn);
        Assert.False(row.IsConfirmedReachable);
        Assert.False(row.IsNotFoundReachable);
        Assert.False(row.IsCheckFailed);
    }

    [Fact]
    public async Task ACheckFailedValue_SetsReachabilityAndShowsTheDetail()
    {
        // AC-499: a source whose call ran but failed for a reason other than "needs sign-in" — kept apart from
        // NotSignedIn precisely so this never reads as "go sign in again" for a check that already reached the
        // connection.
        var depotSource = _DepotSourceWithCheck((_, _) =>
            Task.FromResult(ProjectMemorySourceReachabilityResult.CheckFailed("connection reset")));
        var project = Project.Create("Cockpit") with { Resources = [new ProjectResource("depot:cockpit", ProjectResourceRole.Memory)] };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);
        var row = viewModel.ResourceRows.Single();

        Assert.Equal(ProjectMemorySourceReachability.CheckFailed, row.Reachability);
        Assert.True(row.IsCheckFailed);
        Assert.False(row.IsConfirmedReachable);
        Assert.False(row.IsNotFoundReachable);
        Assert.False(row.IsNotSignedIn);
        Assert.Equal("connection reset", row.ReachabilityDetail);
    }

    [Fact]
    public async Task ACheckDelegateThatThrows_MapsToCheckFailed_NeverToNotFoundOrNotSignedIn()
    {
        // AC-499: a plugin's own check delegate failing (a hiccup in its host call, an unhandled edge case) is the
        // check itself failing to run, not a "needs sign-in" answer — CheckFailed is what exists for exactly this,
        // and it must still never read as "this does not exist" (AC-503 acceptance criterion 4), which would name
        // the wrong cause.
        var depotSource = _DepotSourceWithCheck((_, _) => throw new InvalidOperationException("boom"));
        var project = Project.Create("Cockpit") with { Resources = [new ProjectResource("depot:cockpit", ProjectResourceRole.Memory)] };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);
        var row = viewModel.ResourceRows.Single();

        Assert.Equal(ProjectMemorySourceReachability.CheckFailed, row.Reachability);
        Assert.Equal("boom", row.ReachabilityDetail);
    }

    [Fact]
    public async Task IronLaw8_ANotSignedInResultsDetail_NeverSurfacesOnTheRow_EvenIfThePluginSetOne()
    {
        // Belt-and-braces: even if a plugin's own check mistakenly attached a Detail to a NotSignedIn result — here
        // standing in for what a leaked credential fragment would look like — the row must never show it.
        // ProjectMemorySourceReachabilityResult's own doc comment already says Detail is ignored for NotSignedIn/
        // NotFound; this proves the dialog's own mapping actually honours that rather than merely documenting it.
        var depotSource = _DepotSourceWithCheck((_, _) =>
            Task.FromResult(new ProjectMemorySourceReachabilityResult(ProjectMemorySourceReachability.NotSignedIn, "Bearer fake-token-should-never-be-shown")));
        var project = Project.Create("Cockpit") with { Resources = [new ProjectResource("depot:cockpit", ProjectResourceRole.Memory)] };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), memorySources: [depotSource]);
        var row = viewModel.ResourceRows.Single();

        Assert.Null(row.ReachabilityDetail);
    }

    // --- AC-503 acceptance criterion 6, the two reset sub-cases the review round asked to see proven directly ------
    // (rather than only inferred from OnRoleChanged/OnSelectedMemorySourceChoiceChanged unconditionally calling
    // _ResetReachability): a role switch away from Memory and back, and a source switch that leaves Reference's own
    // text untouched. Built directly against ProjectResourceRowViewModel rather than through CreateAsync, since
    // what is under test here is the row's own reset behavior, not the dialog's diagnostics pipeline.

    [Fact]
    public void SwitchingRoleAwayFromMemoryAndBackToMemory_ResetsReachability()
    {
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Depot project", "depot"),
        };
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory, "cockpit")
        {
            SelectedMemorySourceChoice = choices[1],
            Reachability = ProjectMemorySourceReachability.Confirmed,
            ReachabilityDetail = "24 documents",
        };

        row.Role = ProjectResourceRole.Reference;
        Assert.Null(row.Reachability);

        // Set again after the away-switch already cleared it, so the back-switch below is the one actually proven —
        // a false pass here would mean the first switch's reset alone was doing the work.
        row.Reachability = ProjectMemorySourceReachability.Confirmed;
        row.ReachabilityDetail = "24 documents";

        row.Role = ProjectResourceRole.Memory;

        Assert.Null(row.Reachability);
        Assert.Null(row.ReachabilityDetail);
    }

    [Fact]
    public void SwitchingSelectedMemorySourceChoice_WhileReferenceTextStaysTheSame_ResetsReachability()
    {
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Depot project — Synvolution", "depot"),
            new("Depot project — Wispslate", "depot.wispslate"),
        };
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory, "cockpit")
        {
            SelectedMemorySourceChoice = choices[1],
            Reachability = ProjectMemorySourceReachability.Confirmed,
            ReachabilityDetail = "24 documents",
        };

        // The typed value is deliberately left exactly as it was — a Reachability answer for "cockpit" against
        // Synvolution says nothing about whether "cockpit" also resolves on Wispslate, so switching the source
        // alone (Reference untouched) must invalidate it just as much as an edited Reference would.
        row.SelectedMemorySourceChoice = choices[2];

        Assert.Equal("cockpit", row.Reference);
        Assert.Null(row.Reachability);
        Assert.Null(row.ReachabilityDetail);
    }

    [Fact]
    public async Task RapidEdits_CancelTheOlderInFlightCheck_NotJustDiscardItsResult()
    {
        // AC-503 acceptance criterion 5: simulates a quick run of edits (typing). The older check must actually be
        // cancelled — its own CancellationToken tripped — not merely have its eventual answer thrown away, which is
        // what _RunReachabilityCheckAsync's own version-guard would do for free even without real cancellation.
        var log = new List<(string Value, CancellationToken Token)>();

        // Signalled the moment check("first") is entered. The test waits on this rather than on a stretch of wall
        // clock: what it needs is that the first check has actually started, and a timer only ever approximates
        // that. A 600 ms sleep meant to cover a 400 ms quiet period was enough on a developer's machine and not on
        // a loaded CI runner, where the debounce continuation had not been scheduled yet — the assertion below then
        // failed looking for a log entry that was still on its way.
        var firstCheckStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var depotSource = _DepotSourceWithCheck(async (value, token) =>
        {
            log.Add((value, token));
            if (value == "first")
            {
                firstCheckStarted.TrySetResult();

                // Long enough that, absent real cancellation, this would still be running when the test's own
                // await below returns — proving the token itself was tripped, not just outrun.
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            }

            return ProjectMemorySourceReachabilityResult.Confirmed(value);
        });
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog(), memorySources: [depotSource]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];

        row.Reference = "first";
        // Wait for check("first") to be in flight before the next edit lands — the scenario this AC means, not
        // merely two edits that both fall inside one quiet period. The timeout is a failure mode, not a wait: if
        // the check never starts the test says so instead of hanging, and it is far longer than the quiet period
        // so a slow machine cannot reach it.
        await firstCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        row.Reference = "second";

        // Cancelling the previous run's token happens synchronously at the top of the new _RunResourceDiagnosticsAsync
        // call (before its own quiet-period await), so this is already true the instant the property setter above returns.
        Assert.True(log.Single(entry => entry.Value == "first").Token.IsCancellationRequested,
            "the older check's own token must be cancelled, not merely have its eventual result ignored");

        await viewModel.ResourceDiagnosticsRefreshCompleted;

        Assert.Equal(ProjectMemorySourceReachability.Confirmed, row.Reachability);
        Assert.Equal("second", row.ReachabilityDetail);
    }

    // --- AC-499: the second (family instance) axis — role switches, resets, and CanBrowse/IsMemoryFolderMode -------
    // Built directly against ProjectResourceRowViewModel's own constructor, the same way the two AC-503 reset tests
    // above are, since what is under test here is the row's own behaviour, not CreateAsync's building of it
    // (ProjectDialogMemorySourceFamilyTests covers that half).

    private static (ObservableCollection<MemorySourceChoice> Choices, Dictionary<string, IReadOnlyList<MemorySourceChoice>> Instances) _DepotFamilyChoices()
    {
        var depotInstance = new MemorySourceChoice("Depot (krahwinkel-it)", "depot");
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Depot", Scheme: null) { FamilyKey = "depot", EmptyHint = "No Depot server configured yet" },
        };
        var instances = new Dictionary<string, IReadOnlyList<MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot"] = [depotInstance],
        };
        return (choices, instances);
    }

    [Fact]
    public void SwitchingRoleAwayFromMemory_WithAFamilyInstancePicked_FoldsTheInstancesSchemeAndClearsTheSelection()
    {
        var (choices, instances) = _DepotFamilyChoices();
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory, "cockpit", familyInstanceChoicesByKey: instances)
        {
            SelectedMemorySourceChoice = choices[1],
        };
        Assert.Equal("depot", row.SelectedFamilyInstance?.Scheme); // must actually have an instance picked already

        row.Role = ProjectResourceRole.Reference;

        // What the picker folded away must land in the box the operator can still see.
        Assert.Equal("depot:cockpit", row.Reference);
        // Neither axis may keep pointing at something the box no longer names.
        Assert.Equal(choices[0], row.SelectedMemorySourceChoice);
        Assert.Null(row.SelectedFamilyInstance);
    }

    [Fact]
    public void SwitchingRoleToMemory_WithAFamilyInstanceSchemeAlreadyTyped_SelectsTheFamilyAndInstanceAndShowsTheBareValue()
    {
        var (choices, instances) = _DepotFamilyChoices();
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Reference, "depot:cockpit", familyInstanceChoicesByKey: instances);

        row.Role = ProjectResourceRole.Memory;

        Assert.Equal("depot", row.SelectedMemorySourceChoice?.FamilyKey);
        Assert.Equal("depot", row.SelectedFamilyInstance?.Scheme);
        Assert.Equal("cockpit", row.Reference);
    }

    [Fact]
    public void SwitchingSelectedFamilyInstance_ResetsReachability()
    {
        var depotInstanceA = new MemorySourceChoice("Depot (krahwinkel-it)", "depot");
        var depotInstanceB = new MemorySourceChoice("Depot (synvolution)", "depot.synvolution");
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Depot", Scheme: null) { FamilyKey = "depot" },
        };
        var instances = new Dictionary<string, IReadOnlyList<MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot"] = [depotInstanceA, depotInstanceB],
        };
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory, "cockpit", familyInstanceChoicesByKey: instances)
        {
            SelectedMemorySourceChoice = choices[1],
            Reachability = ProjectMemorySourceReachability.Confirmed,
            ReachabilityDetail = "24 documents",
        };
        Assert.Equal(depotInstanceA, row.SelectedFamilyInstance); // the family's first instance, picked by default

        row.SelectedFamilyInstance = depotInstanceB;

        Assert.Null(row.Reachability);
        Assert.Null(row.ReachabilityDetail);
    }

    [Fact]
    public void SwitchingSelectedMemorySourceChoiceToADifferentFamily_ResetsSelectedFamilyInstanceToTheNewFamilysFirstInstance()
    {
        var depotInstance = new MemorySourceChoice("Depot (krahwinkel-it)", "depot");
        var notesInstance = new MemorySourceChoice("Notes vault", "notes");
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Depot", Scheme: null) { FamilyKey = "depot" },
            new("Notes", Scheme: null) { FamilyKey = "notes" },
        };
        var instances = new Dictionary<string, IReadOnlyList<MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot"] = [depotInstance],
            ["notes"] = [notesInstance],
        };
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory, familyInstanceChoicesByKey: instances)
        {
            SelectedMemorySourceChoice = choices[1],
        };
        Assert.Equal(depotInstance, row.SelectedFamilyInstance);

        row.SelectedMemorySourceChoice = choices[2];

        Assert.Equal(notesInstance, row.SelectedFamilyInstance);
    }

    [Fact]
    public void SwitchingSelectedMemorySourceChoiceFromAFamilyToFolder_ClearsSelectedFamilyInstance()
    {
        var (choices, instances) = _DepotFamilyChoices();
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory, familyInstanceChoicesByKey: instances)
        {
            SelectedMemorySourceChoice = choices[1],
        };
        Assert.NotNull(row.SelectedFamilyInstance);

        row.SelectedMemorySourceChoice = choices[0];

        Assert.Null(row.SelectedFamilyInstance);
    }

    [Fact]
    public void IsMemoryFolderMode_AFamilyPicked_IsFalse_EvenWithNoInstanceChosenYet()
    {
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Depot", Scheme: null) { FamilyKey = "depot" },
        };
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory)
        {
            SelectedMemorySourceChoice = choices[1],
        };

        // No instance registered under "depot" at all (an empty familyInstanceChoicesByKey), so SelectedFamilyInstance
        // stays null — proving this reads "not a folder" from the family choice alone, not from having an instance.
        Assert.Null(row.SelectedFamilyInstance);
        Assert.False(row.IsMemoryFolderMode);
    }

    [Fact]
    public void CanBrowse_AFamilyWithNoInstancePicked_IsFalse()
    {
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Depot", Scheme: null) { FamilyKey = "depot" },
        };
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory)
        {
            SelectedMemorySourceChoice = choices[1],
        };

        // Nothing to browse until an instance exists to browse it with.
        Assert.False(row.CanBrowse);
    }

    [Fact]
    public void CanBrowse_AFamilyInstanceThatCanListLocations_IsTrue()
    {
        var depotInstance = new MemorySourceChoice("Depot (krahwinkel-it)", "depot")
        {
            ListLocationsAsync = _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([])),
        };
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Depot", Scheme: null) { FamilyKey = "depot" },
        };
        var instances = new Dictionary<string, IReadOnlyList<MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot"] = [depotInstance],
        };
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory, familyInstanceChoicesByKey: instances)
        {
            SelectedMemorySourceChoice = choices[1],
        };

        Assert.True(row.CanBrowse);
    }

    [Fact]
    public void ShowsMemorySourceServerRow_IsFalseForFolderAndForAnUngroupedSource()
    {
        var choices = new ObservableCollection<MemorySourceChoice>
        {
            new("Folder", Scheme: null),
            new("Scratchpad", "scratch"),
        };
        var row = new ProjectResourceRowViewModel(choices, ProjectResourceRole.Memory);
        Assert.False(row.ShowsMemorySourceServerRow, "Folder is selected by default on a fresh row");

        row.SelectedMemorySourceChoice = choices[1];

        Assert.False(row.ShowsMemorySourceServerRow, "an ungrouped source has no second axis to pick from at all");
    }
}
