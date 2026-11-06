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
    public async Task ResourceRows_ASecretShapedHomeAnchoredReference_IsNeverAskedAbout_AutoIncludedWithContentNeverSent()
    {
        // AC-612 verification (AC-246 harness): a row a real writer would already have kept out of the shared
        // definition entirely — this fixture simulates one reaching here anyway (a hand-edited definition), and
        // proves the reader does not treat it as a question row (it is Home-scope, not Machine) while the domain
        // model's own SendsContent guard (ProjectResource.cs) still refuses its content regardless of what is stored.
        var binding = new SharedProjectBinding("Sneaky")
        {
            Resources =
            [
                new SharedProjectBindingResource("Instructions", "~/.ssh/id_rsa") { Label = "oops" },
                new SharedProjectBindingResource("SomeFutureRole", "docs/x.md"),
            ],
        };
        var source = _SourceReturning(SharedProjectBindingResult.Success(binding));

        var (viewModel, _) = await SharedProjectBindingDialogViewModel.CreateAsync(
            "depot:sneaky", "Work", source, _ProfileStoreWith("Zyra"));
        viewModel!.SelectedProfileLabel = "Zyra";

        Assert.Empty(viewModel.ResourceRows); // not asked about — it is Home-scope by shape

        var project = viewModel.ToProject();
        var row = Assert.Single(project.Resources, resource => resource.Reference == "~/.ssh/id_rsa");
        Assert.False(row.SendsContent); // enforced by the domain model itself, whatever the wire said

        // A row with a role this build does not know falls back to Reference, the least powerful role.
        Assert.Equal(ProjectResourceRole.Reference, Assert.Single(project.Resources, resource => resource.Reference == "docs/x.md").Role);
    }
}
