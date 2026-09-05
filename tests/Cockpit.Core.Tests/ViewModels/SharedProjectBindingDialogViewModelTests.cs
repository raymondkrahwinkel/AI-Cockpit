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
    // AC-798: the id alone is what `CreateAsync` takes — every other field it once read off a whole `SharedProject`
    // came from the binding it reads itself.
    private const string _SharedProject = "depot:handbook";

    // AC-651: a machine-scoped reference is one `Path.IsPathFullyQualified` accepts, and that answer is per-OS — a
    // POSIX path is fully qualified on this repo's Linux CI and not on a Windows dev box. Same seam as
    // `ProjectResourcePathPortabilityTests`.
    private static readonly string _OtherMachineHome = OperatingSystem.IsWindows() ? @"C:\Users\erik" : "/home/erik";
    private static readonly string _ThisMachineHome = OperatingSystem.IsWindows() ? @"C:\Users\raymond" : "/home/raymond";

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

    // True only while a Posted delegate is running — the only way to tell "resumed via the captured context"
    // (ConfigureAwait(true)) apart from "resumed inline on the thread pool" (false) without real thread identity.
    private sealed class _RecordingSyncContext : SynchronizationContext
    {
        internal bool InsidePost { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            InsidePost = true;
            try
            {
                d(state);
            }
            finally
            {
                InsidePost = false;
            }
        }
    }

    // AC-1117/AC-1119: the continuation of profileStore.LoadAsync mutates Profiles, an ObservableCollection
    // Avalonia binds to, so it must resume on the context CreateAsync was called on, not the thread pool. The
    // fake below awaits a real Task.Delay — NSubstitute's default synchronous completion would hide this bug.
    [Fact]
    public async Task CreateAsync_ProfileStoreLoadCompletesAsynchronously_ResumesTheProfilesMutationOnTheCallingContext()
    {
        var source = _SourceReturning(SharedProjectBindingResult.Success(new SharedProjectBinding("Migratie-2026")));
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await Task.Delay(20).ConfigureAwait(false);
            return (IReadOnlyList<SessionProfile>)[new SessionProfile("Zyra", new ClaudeConfig("/home/someone/.claude"))];
        });

        var previous = SynchronizationContext.Current;
        var context = new _RecordingSyncContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = SharedProjectBindingDialogViewModel.CreateAsync(_SharedProject, "Work", source, store);
            var tcs = new TaskCompletionSource<bool>();
            _ = task.ContinueWith(_ => tcs.TrySetResult(context.InsidePost), TaskContinuationOptions.ExecuteSynchronously);

            Assert.True(await tcs.Task);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
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
    public async Task SaveCommand_AProfileIsPicked_BecomesExecutableAndSaysSo()
    {
        // AC-992: the button binds to SaveCommand, not CanSave — without a CanExecuteChanged it stays greyed out.
        var source = _SourceReturning(SharedProjectBindingResult.Success(new SharedProjectBinding("Migratie-2026")));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Work", source, _ProfileStoreWith("Zyra"));

        Assert.False(viewModel!.SaveCommand.CanExecute(null));
        var raised = 0;
        viewModel.SaveCommand.CanExecuteChanged += (_, _) => raised++;

        viewModel.SelectedProfileLabel = "Zyra";

        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(raised > 0);
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
        Assert.Equal("depot:handbook", project.MemoryRef);
    }

    /// <summary>
    /// AC-1071 acceptance criterion 5: binding never sets an assistant, however the shared definition is shaped —
    /// whoever binds keeps their own. This is the case the ticket came from: Lionear bound EWB and inherited
    /// "Gebruik Zyra", a persona he does not use.
    /// </summary>
    [Fact]
    public async Task ToProject_HoweverTheSharedDefinitionIsShaped_NeverSetsAnAssistant()
    {
        var binding = new SharedProjectBinding("EWB") { BehaviorPrompt = "Gebruik Zyra" };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Work", source, _ProfileStoreWith("Vex"));
        viewModel!.SelectedProfileLabel = "Vex";

        Assert.Null(viewModel.ToProject().Assistant);
    }

    /// <summary>
    /// The same rule one type earlier: the binding a plugin hands the host structurally cannot carry an assistant,
    /// so no source can reintroduce one behind the dialog's back.
    /// </summary>
    [Fact]
    public void SharedProjectBinding_CarriesNoAssistant_SinceTheAssistantNeverTravels()
    {
        var names = typeof(SharedProjectBinding).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Assistant", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AC-1071 acceptance criterion 9: the behaviour prompt is no longer copied unseen. What the operator leaves in
    /// the box is what lands in their own project — not what the shared definition said.
    /// </summary>
    [Fact]
    public async Task ToProject_TheBehaviourPromptWasEditedHere_TakesWhatTheOperatorLeftRatherThanTheSharedText()
    {
        var binding = new SharedProjectBinding("EWB") { BehaviorPrompt = "Gebruik Zyra" };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Work", source, _ProfileStoreWith("Vex"));
        viewModel!.SelectedProfileLabel = "Vex";

        Assert.Equal("Gebruik Zyra", viewModel.BehaviorPrompt);

        viewModel.BehaviorPrompt = "Test before opening a PR.";

        Assert.Equal("Test before opening a PR.", viewModel.ToProject().BehaviorPrompt);
    }

    [Fact]
    public async Task ToProject_TheBehaviourPromptWasClearedHere_LandsAsNoneAtAll()
    {
        var binding = new SharedProjectBinding("EWB") { BehaviorPrompt = "Gebruik Zyra" };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Work", source, _ProfileStoreWith("Vex"));
        viewModel!.SelectedProfileLabel = "Vex";

        viewModel.BehaviorPrompt = "   ";

        Assert.Null(viewModel.ToProject().BehaviorPrompt);
    }

    [Fact]
    public async Task ToProject_ABindingWithLogoBytes_MaterializesThemAsTheLogoPath()
    {
        // AC-763: ProjectsViewModel._WithStoredLogoAsync only knows how to copy from a path or URL, so ToProject
        // must bridge the downloaded bytes into a file it can read.
        var bytes = new byte[] { 137, 80, 78, 71 };
        var source = _SourceReturning(SharedProjectBindingResult.Success(new SharedProjectBinding("Handbook") { LogoBytes = bytes }));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        var project = viewModel.ToProject();

        Assert.False(string.IsNullOrEmpty(project.LogoPath));
        Assert.True(File.Exists(project.LogoPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(project.LogoPath!));
        File.Delete(project.LogoPath!);
    }

    [Fact]
    public async Task ToProject_ABindingWithNoLogo_LeavesLogoPathNull()
    {
        var source = _SourceReturning(SharedProjectBindingResult.Success(new SharedProjectBinding("Handbook")));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            _SharedProject, "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        var project = viewModel.ToProject();

        Assert.Null(project.LogoPath);
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
            "depot:cockpit", "Work", source, _ProfileStoreWith("Zyra"));
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
            .Select(i => new SharedProjectBindingResource("Reference", Path.Combine(_OtherMachineHome, "notes", $"{i}.md")) { Label = $"Note {i}" })
            .ToList();
        var binding = new SharedProjectBinding("Ten Absolute") { Resources = resources };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            "depot:ten", "Work", source, _ProfileStoreWith("Zyra"));
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
            "depot:ten-placeholders", "Work", source, _ProfileStoreWith("Zyra"));
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
            "depot:bare", "Work", source, _ProfileStoreWith("Zyra"));

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
            "depot:portable", "Work", source, _ProfileStoreWith("Zyra"));
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
            "depot:sneaky", "Work", source, _ProfileStoreWith("Zyra"));
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
            "depot:weird", "Work", source, _ProfileStoreWith("Zyra"));
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
            Resources = [new SharedProjectBindingResource("Reference", Path.Combine(_OtherMachineHome, "x.md")) { Label = "Erik's notes" }],
        };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            "depot:has-ask-row", "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";
        var row = Assert.Single(viewModel.ResourceRows);
        Assert.Equal(string.Empty, row.Reference); // never pre-filled with the other machine's path

        var project = viewModel.ToProject();
        Assert.Single(project.Resources); // only the binding marker — the skipped row never made it

        var ownPath = Path.Combine(_ThisMachineHome, "x.md");
        row.Reference = ownPath;
        var filled = viewModel.ToProject();
        Assert.Equal(2, filled.Resources.Count);
        Assert.Contains(filled.Resources, r => r.Reference == ownPath && r.Label == "Erik's notes");
    }

    [Fact]
    public async Task ToProject_EnabledMcpServerNames_CarryThroughAsTheOverlay()
    {
        var binding = new SharedProjectBinding("Overlay") { EnabledMcpServerNames = ["github", "youtrack"] };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            "depot:overlay", "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        var project = viewModel.ToProject();

        Assert.Equal(["github", "youtrack"], project.McpOverlay.EnabledServerNames);
    }

    [Fact]
    public async Task ApplyPickedDirectory_FromAPlainChoose_LeavesGitUrlAloneUnlessCloneWasUsed()
    {
        var binding = new SharedProjectBinding("Handbook") { GitUrl = "git@github.com:example/handbook.git" };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));
        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            "depot:handbook2", "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";
        Assert.True(viewModel.HasGitUrl);

        // "Choose…" on an existing, unrelated folder — no clone happened.
        viewModel.ApplyPickedDirectory("/home/raymond/somewhere-else", gitUrl: null);

        var project = viewModel.ToProject();
        Assert.Equal("/home/raymond/somewhere-else", project.SourceDirectory);
        Assert.Null(project.GitUrl);
    }
}
