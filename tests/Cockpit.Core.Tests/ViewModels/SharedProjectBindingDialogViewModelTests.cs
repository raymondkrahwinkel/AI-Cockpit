using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The "Finish setting up…" bind step (AC-246): <see cref="SharedProjectBindingDialogViewModel"/>. Every fixture
/// goes through <see cref="SharedProjectBindingDialogViewModel.CreateAsync"/> with a real <see cref="SharedProjectBinding"/>
/// built by hand — the shape a plugin's own <c>PrepareBindingAsync</c> would hand back, not a shortcut into the
/// private constructor.
/// </summary>
public class SharedProjectBindingDialogViewModelTests
{
    private static readonly SharedProject _SharedProject = new("depot:payroll", "PayrollProcessor");

    private static ISessionProfileStore _ProfileStoreWith(params string[] labels)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(labels.Select(label => new SessionProfile(label, new ClaudeConfig("/home/someone/.claude"))).ToList());
        return store;
    }

    private static ISharedProjectSource _SourceReturning(SharedProjectBindingResult result)
    {
        var source = Substitute.For<ISharedProjectSource>();
        source.PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        return source;
    }

    [Fact]
    public async Task CreateAsync_TheReadFails_ReturnsNoViewModelAndTheError()
    {
        var source = _SourceReturning(SharedProjectBindingResult.Failed("Sign in to this Depot connection."));

        var (viewModel, error) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Depot — Work", source, _ProfileStoreWith());

        Assert.Null(viewModel);
        Assert.Equal("Sign in to this Depot connection.", error);
    }

    [Fact]
    public async Task CanSave_OnlyAProfileIsRequired_NotAFolder()
    {
        // AC-246 decision (2026-08-02): Folder is an offer, never a gate — the Migratie-2026 case (no gitUrl, no
        // files of its own) must still be able to save with only a profile picked.
        var source = _SourceReturning(SharedProjectBindingResult.Success(new SharedProjectBinding("Migratie-2026")));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Work", source, _ProfileStoreWith("Zyra"));

        Assert.False(viewModel!.CanSave);

        viewModel.SelectedProfileLabel = "Zyra";

        Assert.True(viewModel.CanSave);
        Assert.True(string.IsNullOrEmpty(viewModel.SourceDirectory));
    }

    [Fact]
    public async Task ToProject_ANotesOnlyProjectWithNoGitUrlAndNoFolder_StillProducesAStartableProject()
    {
        var source = _SourceReturning(SharedProjectBindingResult.Success(new SharedProjectBinding("Migratie-2026")));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        var project = viewModel.ToProject();

        Assert.Equal("Migratie-2026", project.Name);
        Assert.Null(project.SourceDirectory);
        Assert.Null(project.GitUrl);
        Assert.Equal("Zyra", project.DefaultProfileLabel);
        // Started with the right profile and the shared memory reference — nothing more required.
        Assert.Equal("depot:payroll", project.MemoryRef);
    }

    [Fact]
    public async Task ToProject_TheBindingRow_IsAlwaysTheFirstMemoryRow()
    {
        // This is exactly what ProjectsViewModel.LoadSharedProjectsAsync/_ClaimBoundProjects reads (boundIds,
        // Project.MemoryRef) to recognise this project as bound and claim its shared fields' origin (AC-604/AC-245)
        // — a garantie in the doc comment, pinned here against the constant it names, not "some Memory row somewhere".
        var binding = new SharedProjectBinding("Cockpit")
        {
            Resources = [new SharedProjectBindingResource("Memory", "~/Notes/cockpit")], // a hand-edited, unusual definition
        };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:cockpit", "Cockpit"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        var project = viewModel.ToProject();

        Assert.Equal("depot:cockpit", project.MemoryRef);
        Assert.Equal(2, project.Resources.Count);
        Assert.Equal(ProjectResourceRole.Memory, project.Resources[0].Role);
        Assert.Equal("depot:cockpit", project.Resources[0].Reference);
    }

    [Fact]
    public async Task ResourceRows_TenNonBlankAbsoluteReferences_AreAllAskedAboutAndNoneAutoIncluded()
    {
        // The purely defensive case: a non-blank absolute reference reaching this dialog anyway (a hand edit, or
        // an older writer that predates AC-246's placeholder shape) — see the sibling test below for the case the
        // real write pipeline actually produces now (a blank reference).
        var resources = Enumerable.Range(1, 10)
            .Select(i => new SharedProjectBindingResource("Reference", $"/home/erik/notes/{i}.md") { Label = $"Note {i}" })
            .ToList();
        var binding = new SharedProjectBinding("Ten Absolute") { Resources = resources };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:ten", "Ten Absolute"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        Assert.Equal(10, viewModel.ResourceRows.Count);
        Assert.True(viewModel.HasResourceRows);
        Assert.All(viewModel.ResourceRows, row => Assert.NotNull(row.OriginalReference)); // a real value was there to show

        // Skipping every row is fine — the project starts without them.
        var project = viewModel.ToProject();
        Assert.Single(project.Resources); // only the binding marker
    }

    [Fact]
    public async Task ResourceRows_TenPlaceholderRows_TheRealWritePipelineCaseNow_AreAllAskedAboutWithNoOriginalReferenceToShow()
    {
        // AC-246 (Raymond, 2026-08-02): this is the normal case now, not a hypothetical — CockpitProjectResourceEntry.Create
        // writes role + label with a blank reference for a machine-scope row. The abstractions-level invariant this
        // reader leans on: a blank SharedProjectBindingResource.Reference IS the placeholder signal (see that
        // record's own remarks) — Create() never produces a non-placeholder row with a blank reference, so there is
        // nothing else a blank Reference here could mean.
        var resources = Enumerable.Range(1, 10)
            .Select(i => new SharedProjectBindingResource("Reference", string.Empty) { Label = $"Note {i}" })
            .ToList();
        var binding = new SharedProjectBinding("Ten Placeholders") { Resources = resources };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:ten-placeholders", "Ten Placeholders"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        Assert.Equal(10, viewModel.ResourceRows.Count);
        Assert.True(viewModel.HasResourceRows);
        // Nothing to show as "was: …" — the writer's own reference never reached this build at all, unlike the
        // defensive non-blank case above.
        Assert.All(viewModel.ResourceRows, row => Assert.Null(row.OriginalReference));
        Assert.Equal(
            Enumerable.Range(1, 10).Select(i => $"Note {i}").OrderBy(l => l, StringComparer.Ordinal),
            viewModel.ResourceRows.Select(row => row.Label).OrderBy(l => l, StringComparer.Ordinal));

        var project = viewModel.ToProject();
        Assert.Single(project.Resources); // only the binding marker — every placeholder skipped, none saved blank
    }

    [Fact]
    public async Task ResourceRows_ZeroResources_IsEmptyAndHiddenFromTheDialog()
    {
        var source = _SourceReturning(SharedProjectBindingResult.Success(new SharedProjectBinding("Bare")));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:bare", "Bare"), "Work", source, _ProfileStoreWith("Zyra"));

        Assert.Empty(viewModel!.ResourceRows);
        Assert.False(viewModel.HasResourceRows);
    }

    [Theory]
    [InlineData("docs/RUNBOOK.md")] // Repo
    [InlineData("~/Notes/cockpit.md")] // Home
    [InlineData("depot:other-project")] // Instance (plugin-source)
    public async Task ResourceRows_AlreadyPortableReferences_AreNeverAskedAbout_AutoIncludedInstead(string reference)
    {
        // AC-605's table: only an absolute (Machine-scope) reference needs asking. This also covers "een `~`-rij"
        // and "een `depot:`-rij" from the AC-246 harness — neither must appear as a question row.
        var binding = new SharedProjectBinding("Portable") { Resources = [new SharedProjectBindingResource("Reference", reference)] };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:portable", "Portable"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        Assert.Empty(viewModel.ResourceRows);

        var project = viewModel.ToProject();
        Assert.Contains(project.Resources, resource => resource.Reference == reference);
    }

    [Fact]
    public async Task ResourceRows_ASecretShapedHomeAnchoredReference_IsNeverAskedAbout_AutoIncludedWithContentNeverSent()
    {
        // AC-612 verification (AC-246 harness): a row a real writer would already have kept out of the shared
        // definition entirely — this fixture simulates one reaching here anyway (a hand-edited definition), and
        // proves the reader does not treat it as a question row (it is Home-scope, not Machine) while the domain
        // model's own SendsContent guard (ProjectResource.cs) still refuses its content regardless of what is stored.
        var binding = new SharedProjectBinding("Sneaky")
        {
            Resources = [new SharedProjectBindingResource("Instructions", "~/.ssh/id_rsa") { Label = "oops" }],
        };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:sneaky", "Sneaky"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        Assert.Empty(viewModel.ResourceRows); // not asked about — it is Home-scope by shape

        var project = viewModel.ToProject();
        var row = Assert.Single(project.Resources, resource => resource.Reference == "~/.ssh/id_rsa");
        Assert.False(row.SendsContent); // enforced by the domain model itself, whatever the wire said
    }

    [Fact]
    public async Task ResourceRows_ARowWithAnUnrecognisedRole_FallsBackToReference_TheLeastPowerfulRole()
    {
        var binding = new SharedProjectBinding("Weird") { Resources = [new SharedProjectBindingResource("SomeFutureRole", "docs/x.md")] };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:weird", "Weird"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        var project = viewModel.ToProject();
        var row = Assert.Single(project.Resources, resource => resource.Reference == "docs/x.md");
        Assert.Equal(ProjectResourceRole.Reference, row.Role);
    }

    [Fact]
    public async Task ToProject_ASkippedAskRow_IsDroppedRatherThanSavedBlank()
    {
        var binding = new SharedProjectBinding("Has Ask Row")
        {
            Resources = [new SharedProjectBindingResource("Reference", "/home/erik/x.md") { Label = "Erik's notes" }],
        };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:has-ask-row", "Has Ask Row"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";
        var row = Assert.Single(viewModel.ResourceRows);
        Assert.Equal(string.Empty, row.Reference); // never pre-filled with the other machine's path

        var project = viewModel.ToProject();
        Assert.Single(project.Resources); // only the binding marker — the skipped row never made it

        row.Reference = "/home/raymond/x.md";
        var filled = viewModel.ToProject();
        Assert.Equal(2, filled.Resources.Count);
        Assert.Contains(filled.Resources, r => r.Reference == "/home/raymond/x.md" && r.Label == "Erik's notes");
    }

    [Fact]
    public async Task ToProject_EnabledMcpServerNames_CarryThroughAsTheOverlay()
    {
        var binding = new SharedProjectBinding("Overlay") { EnabledMcpServerNames = ["github", "youtrack"] };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:overlay", "Overlay"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        var project = viewModel.ToProject();

        Assert.Equal(["github", "youtrack"], project.McpOverlay.EnabledServerNames);
    }

    [Fact]
    public async Task ApplyPickedDirectory_FromAPlainChoose_LeavesGitUrlAloneUnlessCloneWasUsed()
    {
        var binding = new SharedProjectBinding("Payroll") { GitUrl = "git@github.com:synvolution/payroll.git" };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            new SharedProject("depot:payroll2", "Payroll"), "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";
        Assert.True(viewModel.HasGitUrl);

        // "Choose…" on an existing, unrelated folder — no clone happened.
        viewModel.ApplyPickedDirectory("/home/raymond/somewhere-else", gitUrl: null);

        var project = viewModel.ToProject();
        Assert.Equal("/home/raymond/somewhere-else", project.SourceDirectory);
        Assert.Null(project.GitUrl);
    }
}
