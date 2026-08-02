using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The project editor (AC-160): what it opens with, and what it hands back on Save. The overlay it produces is
/// what a project's sessions actually get, so an unticked row that fails to reach the saved project is a server
/// silently still on.
/// </summary>
public class ProjectDialogViewModelTests
{
    private static ISessionProfileStore ProfileStore(params string[] labels)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            labels.Select(label => new SessionProfile(label, new ClaudeConfig("~/.claude"))).ToList());
        return store;
    }

    private static IMcpServerCatalog Catalog(params McpServerConfig[] servers)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(servers);
        return catalog;
    }

    private static McpServerConfig Server(string name) => new() { Name = name, Command = "npx" };

    // AC-485 review (FIX 9): rooted per platform (this repo's CI runs on Linux, this box runs Windows) rather than a
    // literal "D:\handbook" — that literal is not even an absolute path on Linux, so a test using it exercises the
    // probe's and portability's "not fully qualified, skip" branch there and their "fully qualified, check it"
    // branch here, silently running different code on the two platforms that run this same test.
    private static string _Root(params string[] segments) =>
        Path.Combine([OperatingSystem.IsWindows() ? @"C:\Users\raymond\Cockpit" : "/home/raymond/Cockpit", .. segments]);

    [Fact]
    public async Task CreateAsync_NewProject_OpensEmptyWithEveryServerTicked()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore("personal"), Catalog(Server("youtrack"), Server("depot")));

        Assert.False(viewModel.IsEditing);
        Assert.Equal("New project", viewModel.DialogTitle);
        Assert.False(viewModel.CanSave, "a project needs a name");
        Assert.All(viewModel.McpServers, server => Assert.True(server.IsEnabledForSession));
    }

    [Fact]
    public async Task CreateAsync_ADisabledRegistryServer_IsNotOffered()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null,
            ProfileStore("personal"),
            Catalog(Server("depot"), new McpServerConfig { Name = "off", Enabled = false }));

        // A server switched off in the registry reaches no session at all, so offering it here as a per-project
        // toggle would promise a project something it cannot have — and every other picker already leaves it out.
        Assert.Equal(new[] { "depot" }, viewModel.McpServers.Select(server => server.Name));
    }

    [Fact]
    public async Task ToProject_KeepsADisabledNameTheChecklistCannotShow()
    {
        // The project switched "gone" off while it existed; it has since left the registry. Saving must not read the
        // missing row as "switched back on" — that would quietly re-enable a server the operator had turned off.
        var project = Project.Create("Cockpit") with
        {
            McpOverlay = new ProjectMcpOverlay { DisabledServerNames = ["gone"] },
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal"), Catalog(Server("depot")));

        Assert.DoesNotContain("gone", viewModel.ToProject().McpOverlay.EnabledServerNames!);
    }

    /// <summary>The mirror of the above: a server this project had on, whose row the checklist cannot show, must not be switched off by a save.</summary>
    [Fact]
    public async Task ToProject_KeepsAnEnabledNameTheChecklistCannotShow()
    {
        var project = Project.Create("Cockpit") with
        {
            McpOverlay = new ProjectMcpOverlay { EnabledServerNames = ["gone", "depot"] },
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal"), Catalog(Server("depot")));

        Assert.Contains("gone", viewModel.ToProject().McpOverlay.EnabledServerNames!);
    }

    /// <summary>
    /// The point of the whole list (Raymond, 2026-08-01): a project that narrowed its servers is saved as "these are
    /// on", so a server added to the registry afterwards is in nobody's list and starts unticked there — where the
    /// old off-list had it arrive ticked in every project.
    /// </summary>
    [Fact]
    public async Task ToProject_ANarrowedProject_DoesNotTickAServerAddedLater()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(Server("youtrack"), Server("depot")));
        viewModel.Name = "Cockpit";
        viewModel.McpServers.Single(server => server.Name == "depot").IsEnabledForSession = false;

        Assert.False(viewModel.ToProject().McpOverlay.IsSelectedByDefault("brand-new"));
    }

    /// <summary>And the other half: a project that switched nothing off has no opinion, so it still picks new servers up.</summary>
    [Fact]
    public async Task ToProject_AProjectThatNarrowedNothing_StillTicksAServerAddedLater()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(Server("youtrack"), Server("depot")));
        viewModel.Name = "Cockpit";

        var overlay = viewModel.ToProject().McpOverlay;

        Assert.Null(overlay.EnabledServerNames);
        Assert.True(overlay.IsSelectedByDefault("brand-new"));
    }

    [Fact]
    public async Task CreateAsync_InternalServers_AreNotOffered()
    {
        var internalServer = new McpServerConfig { Name = "autopilot-ceo", Internal = true };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(Server("youtrack"), internalServer));

        Assert.Equal(new[] { "youtrack" }, viewModel.McpServers.Select(server => server.Name));
    }

    [Fact]
    public async Task CreateAsync_ExistingProject_UnticksTheServersItTurnedOff()
    {
        var project = Project.Create("Cockpit") with
        {
            McpOverlay = new ProjectMcpOverlay { DisabledServerNames = ["depot"] },
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore("personal"), Catalog(Server("youtrack"), Server("depot")));

        Assert.True(viewModel.IsEditing);
        Assert.False(viewModel.McpServers.Single(server => server.Name == "depot").IsEnabledForSession);
        Assert.True(viewModel.McpServers.Single(server => server.Name == "youtrack").IsEnabledForSession);
    }

    [Fact]
    public async Task CreateAsync_ExistingProject_PreselectsItsProfileByLabel()
    {
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal", "work"), Catalog());

        Assert.Equal("work", viewModel.SelectedProfileLabel);
    }

    /// <summary>A label whose profile was since renamed or removed must not be handed back as a selection nothing resolves.</summary>
    [Fact]
    public async Task CreateAsync_ProfileLabelThatNoLongerExists_LeavesTheSelectionEmpty()
    {
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "deleted" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal"), Catalog());

        Assert.Null(viewModel.SelectedProfileLabel);
    }

    [Fact]
    public async Task ToProject_TickedServers_BecomeTheOverlaysEnabledList()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(Server("youtrack"), Server("depot")));
        viewModel.Name = "Cockpit";
        viewModel.McpServers.Single(server => server.Name == "depot").IsEnabledForSession = false;

        Assert.Equal(new[] { "youtrack" }, viewModel.ToProject().McpOverlay.EnabledServerNames);
    }

    [Fact]
    public async Task ToProject_Editing_KeepsTheIdSoReferencesStillResolve()
    {
        var project = Project.Create("Cockpit");

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.Equal(project.Id, viewModel.ToProject().Id);
    }

    /// <summary>v1 edits which servers are on, not the servers themselves — a project's own servers must survive an edit.</summary>
    [Fact]
    public async Task ToProject_Editing_CarriesTheProjectsOwnServersThrough()
    {
        var project = Project.Create("Cockpit") with
        {
            McpOverlay = new ProjectMcpOverlay { AdditionalServers = [Server("project-tools")] },
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.Equal("project-tools", Assert.Single(viewModel.ToProject().McpOverlay.AdditionalServers).Name);
    }

    /// <summary>Likewise for what v2 writes: editing a project in v1 must not drop its knowledge-store reference (AC-166).</summary>
    [Fact]
    public async Task ToProject_Editing_CarriesTheMemoryReferenceThrough()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:ai-cockpit" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.Equal("depot:ai-cockpit", viewModel.ToProject().MemoryRef);
    }

    /// <summary>
    /// AC-483: this dialog offers exactly one memory box, but a project can carry an Instructions row and a
    /// Reference row too (and this v1 UI has no field for either). Opening the editor and saving without changing
    /// anything must not drop them — the bug this pins built a fresh <c>Project(...)</c> in <c>ToProject</c> and
    /// never set <c>Resources</c> at all, so only the folded-in memory value survived.
    /// </summary>
    [Fact]
    public async Task ToProject_Editing_KeepsEveryResourceRowUntouched()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("/home/raymond/Notes/Cockpit", ProjectResourceRole.Memory),
                new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook" },
                new ProjectResource(_Root("handbook"), ProjectResourceRole.Reference) { Label = "Old handbook" },
            ],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.Equal(project.Resources, viewModel.ToProject().Resources);
    }

    /// <summary>
    /// The other half of the same fix: editing one row's reference must touch only that row, leaving an
    /// Instructions row and a Reference row exactly as they were (AC-485: every row is its own
    /// <see cref="ProjectResourceRowViewModel"/> now, not folded into one dialog-wide value).
    /// </summary>
    [Fact]
    public async Task ToProject_ChangingOneRowsReference_TouchesOnlyThatRow()
    {
        var instructions = new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook" };
        var reference = new ProjectResource(_Root("handbook"), ProjectResourceRole.Reference) { Label = "Old handbook" };
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("/home/raymond/Notes/Cockpit", ProjectResourceRole.Memory),
                instructions,
                reference,
            ],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());
        viewModel.ResourceRows.Single(row => row.Role == ProjectResourceRole.Memory).Reference = "/home/raymond/Notes/CockpitV2";

        var saved = viewModel.ToProject();

        Assert.Equal("/home/raymond/Notes/CockpitV2", saved.MemoryRef);
        // AC-485 review (FIX 9): .Equal pins the whole sequence in the order the operator left it, not merely that
        // the untouched rows still appear somewhere in the saved list.
        Assert.Equal(
            new[]
            {
                new ProjectResource("/home/raymond/Notes/CockpitV2", ProjectResourceRole.Memory),
                instructions,
                reference,
            },
            saved.Resources);
    }

    [Fact]
    public async Task ToProject_BlankOptionalFields_AreStoredAsAbsentRatherThanEmpty()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "  Cockpit  ";
        viewModel.Description = "   ";
        viewModel.BehaviorPrompt = string.Empty;
        viewModel.Category = "   ";

        var project = viewModel.ToProject();

        Assert.Equal("Cockpit", project.Name);
        Assert.Null(project.Description);
        Assert.Null(project.BehaviorPrompt);
        Assert.Null(project.Category);
    }

    // AC-618: category — a plain, never-claimed field on the editor.

    [Fact]
    public async Task CreateAsync_ExistingProjectWithACategory_OpensWithItFilledIn()
    {
        var project = Project.Create("Cockpit") with { Category = "Werk" };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.Equal("Werk", viewModel.Category);
    }

    [Fact]
    public async Task ToProject_ANewCategory_IsTrimmedAndSaved()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.Category = "  Werk  ";

        Assert.Equal("Werk", viewModel.ToProject().Category);
    }

    /// <summary>
    /// Category is never one of the six claimable <c>HostProjectField</c>s (AC-604) — a shared project's own
    /// definition never gets a say in what this operator files it under, so unlike Name/Description above there is
    /// no origin badge to gate this and no <c>_Carry</c> path that could silently keep an edit from landing.
    /// </summary>
    [Fact]
    public async Task ToProject_CategoryIsNeverCarriedFromTheOriginalProject_EvenWhenOtherFieldsAreClaimed()
    {
        var project = Project.Create("Cockpit") with { Name = "Cockpit", Category = "Werk" };
        var ownership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Name] = new ProjectFieldOwnership("Depot — Work"),
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), fieldOwnership: ownership);

        viewModel.Category = "Privé";

        Assert.Equal("Privé", viewModel.ToProject().Category);
    }

    [Fact]
    public async Task ApplyPickedDirectory_FromAClone_KeepsTheUrlBesideThePath()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";

        viewModel.ApplyPickedDirectory("/home/raymond/clones/cockpit", "https://example.test/cockpit.git");

        var project = viewModel.ToProject();
        Assert.Equal("/home/raymond/clones/cockpit", project.SourceDirectory);
        Assert.Equal("https://example.test/cockpit.git", project.GitUrl);
    }

    /// <summary>Pointing an existing project at a folder of its own drops the clone URL, which no longer describes where it came from.</summary>
    [Fact]
    public async Task ApplyPickedDirectory_WithoutAUrl_ClearsAStaleCloneUrl()
    {
        var project = Project.Create("Cockpit") with { GitUrl = "https://example.test/old.git" };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        viewModel.ApplyPickedDirectory("/home/raymond/elsewhere");

        Assert.Null(viewModel.ToProject().GitUrl);
    }

    [Fact]
    public async Task SaveCommand_BecomesAvailableOnlyOnceTheProjectHasAName()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());

        Assert.False(viewModel.SaveCommand.CanExecute(null));
        viewModel.Name = "Cockpit";
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task CreateAsync_ExistingProject_OpensItsInformationRowsInOrder()
    {
        var project = Project.Create("Cockpit") with
        {
            AdditionalInfo =
            [
                new ProjectInfoField("Repository", "https://github.com/example/repo"),
                new ProjectInfoField("Customer", "Acme BV"),
            ],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal"), Catalog());

        Assert.Equal(new[] { "Repository", "Customer" }, viewModel.AdditionalInfo.Select(field => field.Label));
        Assert.Equal("https://github.com/example/repo", viewModel.AdditionalInfo[0].Value);
    }

    [Fact]
    public async Task AddAndRemoveInfoField_AppendAnEmptyRowAndTakeItBack()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());

        viewModel.AddInfoFieldCommand.Execute(null);
        Assert.Empty(Assert.Single(viewModel.AdditionalInfo).Label);

        viewModel.RemoveInfoFieldCommand.Execute(viewModel.AdditionalInfo[0]);
        Assert.Empty(viewModel.AdditionalInfo);
    }

    [Fact]
    public async Task ToProject_DropsAnUntouchedRowAndTidiesTheRest()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AdditionalInfo.Add(new ProjectInfoFieldViewModel("  Repository ", "https://github.com/example/repo\r\n"));
        viewModel.AdditionalInfo.Add(new ProjectInfoFieldViewModel());

        var saved = viewModel.ToProject();

        Assert.Single(saved.AdditionalInfo);
        Assert.Equal(new ProjectInfoField("Repository", "https://github.com/example/repo"), saved.AdditionalInfo[0]);
    }

    [Fact]
    public async Task ToProject_KeepsARowThatHasOnlyAValue()
    {
        // Pasting a link and saving is the fastest thing the editor can do; demanding a label first would be exactly
        // the ceremony this list exists to avoid.
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";
        viewModel.AdditionalInfo.Add(new ProjectInfoFieldViewModel(value: "https://example.test"));

        Assert.True(viewModel.CanSave);
        Assert.Equal("https://example.test", Assert.Single(viewModel.ToProject().AdditionalInfo).Value);
    }

    [Fact]
    public async Task InformationRows_CarryWhetherTheyAreSharedWithSessionsBothWays()
    {
        // The editor is the only place this flag is ever set, and it travels through three positional arguments and a
        // ToDomain initializer to get there and back. Reorder or drop any of them and nothing else in the suite notices.
        var project = Project.Create("Cockpit") with
        {
            AdditionalInfo =
            [
                new ProjectInfoField("Repository", "https://github.com/example/repo") { IsSharedWithSessions = true },
                new ProjectInfoField("Invoice reference", "AC-2026-118"),
            ],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal"), Catalog());

        Assert.Equal(new[] { true, false }, viewModel.AdditionalInfo.Select(field => field.IsSharedWithSessions));

        viewModel.AdditionalInfo[1].IsSharedWithSessions = true;
        Assert.Equal(new[] { true, true }, viewModel.ToProject().AdditionalInfo.Select(field => field.IsSharedWithSessions));
    }

    [Fact]
    public void MarkingARowSecret_UnticksTheSharingItCanNoLongerHave()
    {
        // The domain gate keeps a secret out of a prompt either way; this is the editor not showing a ticked box it
        // is ignoring, and not handing the tick back if the operator unticks Secret again.
        var row = new ProjectInfoFieldViewModel("Deploy token", "s3cr3t", isSharedWithSessions: true);

        row.IsSecret = true;

        Assert.False(row.IsSharedWithSessions);
        Assert.False(row.CanShareWithSessions);
        Assert.False(row.ToDomain().ReachesSessions);

        row.IsSecret = false;
        Assert.False(row.IsSharedWithSessions, "the tick is not silently restored — the operator says so again");
    }

    [Fact]
    public async Task CancelCommand_ClosesWithoutAProject()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        Project? closedWith = Project.Create("sentinel");
        var closed = false;
        viewModel.CloseRequested += project =>
        {
            closedWith = project;
            closed = true;
        };

        viewModel.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Null(closedWith);
    }

    // --- AC-604: project-field ownership --------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_AProjectNoOneClaimed_ShowsNoOriginBadges()
    {
        // Acceptance criterion 4: character-for-character as before — no badge, no locked field, no extra row.
        var project = Project.Create("Cockpit");
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal"), Catalog());

        Assert.False(viewModel.HasFieldOwnership);
        Assert.False(viewModel.NameOrigin.IsClaimed);
        Assert.False(viewModel.NameOrigin.IsLockedHere);
        Assert.Null(viewModel.NameOrigin.ReadOnlyReason);
    }

    [Fact]
    public async Task CreateAsync_AClaimedField_ResolvesItsSourceAndLeavesOtherFieldsLocal()
    {
        var project = Project.Create("Cockpit");
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Name] = new ProjectFieldOwnership("Depot — Work", IsEditable: true),
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore("personal"), Catalog(), fieldOwnership: fieldOwnership);

        Assert.True(viewModel.HasFieldOwnership);
        Assert.True(viewModel.NameOrigin.IsClaimed);
        Assert.Equal("Depot — Work", viewModel.NameOrigin.SourceName);
        // The project has a claim, but nothing named Description here — it stays local, still badged as such since
        // the project overall is a shared one (the mixed case AC-604 exists for).
        Assert.False(viewModel.DescriptionOrigin.IsClaimed);
    }

    [Fact]
    public async Task CreateAsync_AnEditableClaimedField_IsStillLockedUntilThereIsSomewhereToWriteBackTo()
    {
        // Regression (Raymond, review of the first render): ProjectFieldOwnership.IsEditable: true used to unlock
        // the control while there was still nowhere for an edit to go — the operator's typing vanished on Save
        // without a word about it. Until AC-247 gives an editable claim a write-back destination, every claimed
        // field is locked regardless of IsEditable.
        var project = Project.Create("Cockpit");
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Behavior] = new ProjectFieldOwnership("Depot — Work", IsEditable: true),
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore("personal"), Catalog(), fieldOwnership: fieldOwnership);

        Assert.True(viewModel.BehaviorOrigin.IsLockedHere, "an editable claim must not offer an edit with nowhere to go");
        Assert.Contains("Depot — Work", viewModel.BehaviorOrigin.ReadOnlyReason);
    }

    [Fact]
    public async Task CreateAsync_AReadOnlyClaimedField_LocksTheControlAndExplainsWhy()
    {
        var project = Project.Create("Cockpit");
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Behavior] = new ProjectFieldOwnership("EVE Workbench — Team", IsEditable: false),
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore("personal"), Catalog(), fieldOwnership: fieldOwnership);

        Assert.True(viewModel.BehaviorOrigin.IsLockedHere);
        Assert.Contains("EVE Workbench — Team", viewModel.BehaviorOrigin.ReadOnlyReason);
    }

    [Fact]
    public async Task ToProject_AClaimedField_CarriesTheOriginalValueRatherThanTheLocalEdit()
    {
        // Acceptance criterion 3: an edit to a claimed field must never reach cockpit.json. Editable=true here on
        // purpose — even a field the control lets the operator type into must not save that edit, since the write
        // destination for an editable claim is the plugin, not cockpit.json (not built by this seam ticket).
        var project = Project.Create("Cockpit") with { Name = "Original name" };
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Name] = new ProjectFieldOwnership("Depot — Work", IsEditable: true),
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore("personal"), Catalog(), fieldOwnership: fieldOwnership);

        viewModel.Name = "Edited locally";

        Assert.Equal("Original name", viewModel.ToProject().Name);
    }

    [Fact]
    public async Task ToProject_AnUnclaimedField_StillSavesTheLocalEdit()
    {
        // The mirror of the guard above: a field this project's claim does not name is not carried — it is an
        // ordinary field, and an edit to it must reach the saved project exactly as it always did.
        var project = Project.Create("Cockpit") with { Description = "Original description" };
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Name] = new ProjectFieldOwnership("Depot — Work"),
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore("personal"), Catalog(), fieldOwnership: fieldOwnership);

        viewModel.Description = "Edited locally";

        Assert.Equal("Edited locally", viewModel.ToProject().Description);
    }
}
