using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-620's confirmation screen. The DoD's own bar: a field the project marks as secret must never leave the
/// machine unencrypted — tested here as the wire content <see cref="ISharedProjectSource.PublishAsync"/> actually
/// receives, not the intent, the same "measured, not guessed" discipline this ticket's own DoD names.
/// </summary>
[Collection("avalonia")]
public class ShareProjectDialogViewModelTests
{
    private const string SecretValue = "hunter2-super-secret-value";

    private static Project Project(IReadOnlyList<ProjectResource>? resources = null, IReadOnlyList<ProjectInfoField>? additionalInfo = null) =>
        Core.Projects.Project.Create("PayrollProcessor") with
        {
            Description = "Loonverwerking",
            GitUrl = "git@github.com:example/payroll.git",
            SourceDirectory = "/home/raymond/RiderProjects/payroll",
            DefaultProfileLabel = "Zyra — Sonnet",
            Resources = resources ?? [],
            AdditionalInfo = additionalInfo ?? [],
        };

    private static ISharedProjectSource FakeSource(SharedProjectPublishResult result, out Func<SharedProjectPublishDefinition> sent)
    {
        var source = Substitute.For<ISharedProjectSource>();
        source.SourceName.Returns("Work");
        source.CanPublish.Returns(true);
        SharedProjectPublishDefinition? captured = null;
        source.PublishAsync(Arg.Any<string>(), Arg.Any<SharedProjectPublishDefinition>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.ArgAt<SharedProjectPublishDefinition>(1);
                return Task.FromResult(result);
            });
        source.ListPublishTargetsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SharedProjectPublishTargetListResult.Success(
                [new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner")])));
        sent = () => captured ?? throw new InvalidOperationException("PublishAsync was never called");
        return source;
    }

    [Fact]
    public void Rows_PortableResource_GoesToDepot()
    {
        var project = Project(resources: [new ProjectResource("docs/CONVENTIONS.md", ProjectResourceRole.Reference) { Label = "Conventions" }]);
        var viewModel = ShareProjectDialogViewModel.Create(project, []);

        Assert.Contains(viewModel.GoesToDepot, row => row.Label == "Conventions" && row.Value == "docs/CONVENTIONS.md");
        Assert.DoesNotContain(viewModel.StaysOnThisMachine, row => row.Label == "Conventions");
    }

    [Fact]
    public void Rows_MachineScopeResource_TravelsAsAPlaceholderAndKeepsItsPathLocal()
    {
        // ClassifyScope's own Path.IsPathFullyQualified check only recognises this platform's own absolute-path
        // shape (documented in ProjectResourcePathPortability's class remarks) — a hardcoded POSIX path here would
        // read as Repo-scoped rather than Machine-scoped on Windows, so this builds one this runtime actually calls
        // fully qualified.
        var machinePath = Path.Combine(Path.GetTempPath(), "dumps", "payroll-2026.sql");
        var project = Project(resources:
            [new ProjectResource(machinePath, ProjectResourceRole.Reference) { Label = "Testdata dump" }]);
        var viewModel = ShareProjectDialogViewModel.Create(project, []);

        Assert.Contains(viewModel.StaysOnThisMachine, row => row.Label == "Testdata dump — path" && row.Value == machinePath);
        Assert.Contains(viewModel.GoesToDepot, row => row.Label == "Testdata dump — name only");
    }

    // AC-699: the reported bug — an unlabelled resource falls back to its role for a name, so one machine-scope
    // Memory row showed up as "Memory" in both columns, once as a path and once as an explanation of a path.
    [Fact]
    public void Rows_UnlabelledMachineScopeResource_NeverRepeatsOneLabelInBothColumns()
    {
        // Same platform-fully-qualified requirement as Rows_MachineScopeResource_TravelsAsAPlaceholderAndKeepsItsPathLocal.
        var machinePath = Path.Combine(Path.GetTempPath(), "Memory", "SynCRM");
        var project = Project(resources:
        [
            new ProjectResource(machinePath, ProjectResourceRole.Memory),
            new ProjectResource("depot:synvolution-flow", ProjectResourceRole.Memory),
        ]);
        var viewModel = ShareProjectDialogViewModel.Create(project, []);

        var shared = viewModel.GoesToDepot.Select(row => row.Label).Intersect(viewModel.StaysOnThisMachine.Select(row => row.Label));
        Assert.Empty(shared);
        Assert.Contains(viewModel.StaysOnThisMachine, row => row.Value == machinePath);
        Assert.Contains(viewModel.GoesToDepot, row => row.Value == "depot:synvolution-flow");
    }

    // The column titles are a promise about the whole project, not only the fields the publish call happens to map.
    [Fact]
    public void Rows_FieldsAPublishedDefinitionHasNoPlaceFor_AreNamedAsStayingHere()
    {
        var project = Project(additionalInfo: [new ProjectInfoField("Repository", "https://github.com/example/payroll")]) with
        {
            Category = "Synvolution",
        };
        var viewModel = ShareProjectDialogViewModel.Create(project, []);

        Assert.Contains(viewModel.StaysOnThisMachine, row => row.Label == "Category" && row.Value == "Synvolution");
        Assert.Contains(viewModel.StaysOnThisMachine, row => row.Label == "Anything else worth keeping");
        Assert.DoesNotContain(viewModel.GoesToDepot, row => row.Label == "Category");
    }

    // AC-763: the logo now travels with the rest of the definition, so it belongs in the column that promises that.
    [Fact]
    public void Rows_ALogo_GoesToDepotRatherThanStayingOnThisMachine()
    {
        var project = Project() with { LogoPath = "/home/raymond/logos/payroll.png" };
        var viewModel = ShareProjectDialogViewModel.Create(project, []);

        Assert.Contains(viewModel.GoesToDepot, row => row.Label == "Logo" && row.Value == "/home/raymond/logos/payroll.png");
        Assert.DoesNotContain(viewModel.StaysOnThisMachine, row => row.Label == "Logo");
    }

    // A connection whose projects the operator may all only read lists nothing to publish to — silence there reads
    // as a broken dropdown, which is exactly how AC-699's role-parsing bug hid.
    [Fact]
    public async Task LoadTargets_ASucceededButEmptyList_SaysSoInsteadOfShowingAnEmptyPicker()
    {
        var source = Substitute.For<ISharedProjectSource>();
        source.SourceName.Returns("Work");
        source.CanPublish.Returns(true);
        source.ListPublishTargetsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SharedProjectPublishTargetListResult.Success([])));

        var viewModel = ShareProjectDialogViewModel.Create(Project(), [source]);
        await Task.Yield();

        Assert.Empty(viewModel.Targets);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.LoadError));
    }

    [Fact]
    public void Rows_SecretShapedResourceReference_StaysOnThisMachineAndNeverShowsTheReferenceItself()
    {
        var project = Project(resources: [new ProjectResource("~/.ssh/id_rsa", ProjectResourceRole.Reference) { Label = "Deploy key" }]);
        var viewModel = ShareProjectDialogViewModel.Create(project, []);

        var row = Assert.Single(viewModel.StaysOnThisMachine, row => row.Label == "Deploy key");
        Assert.DoesNotContain("id_rsa", row.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(viewModel.GoesToDepot, row => row.Label == "Deploy key");
    }

    [Fact]
    public void Rows_AdditionalInfo_NeverAppearsInEitherColumn_SecretOrNot()
    {
        // AdditionalInfo is not part of the portable contract at all yet (CockpitProjectDefinitionSecrecyTests pins
        // this on the write side) — nothing here should promise a row travels, or even name it, secret or plain.
        var project = Project(additionalInfo:
        [
            new ProjectInfoField("Repository", "https://github.com/example/payroll"),
            new ProjectInfoField("Production DB password", SecretValue) { IsSecret = true },
        ]);
        var viewModel = ShareProjectDialogViewModel.Create(project, []);

        Assert.DoesNotContain(viewModel.GoesToDepot, row => row.Label.Contains("Repository") || row.Value.Contains(SecretValue));
        Assert.DoesNotContain(viewModel.StaysOnThisMachine, row => row.Value.Contains(SecretValue));
    }

    [Fact]
    public async Task ShareAsync_Success_ClosesWithTheBindingRowPrependedFirst()
    {
        var project = Project();
        var source = FakeSource(SharedProjectPublishResult.Success("depot:payroll-processor"), out _);
        var viewModel = ShareProjectDialogViewModel.Create(project, [source]);

        Project? closed = null;
        viewModel.CloseRequested += result => closed = result;
        viewModel.SelectedTarget = new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner");

        await viewModel.ShareCommand.ExecuteAsync(null);

        Assert.NotNull(closed);
        var binding = Assert.Single(closed!.Resources);
        Assert.Equal(ProjectResourceRole.Memory, binding.Role);
        Assert.Equal("depot:payroll-processor", binding.Reference);
        // AC-762: the ◆ badge's cold-start fallback is set the moment publishing succeeds.
        Assert.Equal("Work", closed.SharedSourceName);
    }

    // AC-762 bijvangst: sharing an already-shared project used to prepend a second Memory row instead of replacing
    // the first, so "Stop sharing" (which removes only the first match) left the stale row's binding intact.
    [Fact]
    public async Task ShareAsync_Success_AProjectAlreadySharedOnce_ReplacesTheExistingMemoryRowRatherThanStacking()
    {
        var project = Project(resources: [new ProjectResource("depot:payroll-processor-old", ProjectResourceRole.Memory)]);
        var source = FakeSource(SharedProjectPublishResult.Success("depot:payroll-processor"), out _);
        var viewModel = ShareProjectDialogViewModel.Create(project, [source]);

        Project? closed = null;
        viewModel.CloseRequested += result => closed = result;
        viewModel.SelectedTarget = new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner");

        await viewModel.ShareCommand.ExecuteAsync(null);

        Assert.NotNull(closed);
        var binding = Assert.Single(closed!.Resources);
        Assert.Equal(ProjectResourceRole.Memory, binding.Role);
        Assert.Equal("depot:payroll-processor", binding.Reference);
    }

    [Fact]
    public async Task ShareAsync_Failure_DoesNotCloseAndShowsTheError()
    {
        var project = Project();
        var source = FakeSource(SharedProjectPublishResult.PermissionDenied("You do not have permission to publish here."), out _);
        var viewModel = ShareProjectDialogViewModel.Create(project, [source]);

        Project? closed = null;
        var closeRaised = false;
        viewModel.CloseRequested += result => { closed = result; closeRaised = true; };
        viewModel.SelectedTarget = new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner");

        await viewModel.ShareCommand.ExecuteAsync(null);

        Assert.False(closeRaised);
        Assert.Null(closed);
        Assert.Equal("You do not have permission to publish here.", viewModel.ErrorMessage);
    }

    // The DoD's bar, at the boundary that matters most: what actually reaches PublishAsync for a project carrying a
    // secret AdditionalInfo row. The type itself has no field to carry it; this proves the mapping doesn't smuggle
    // it into a field the type does carry.
    [Fact]
    public async Task ShareAsync_PublishedDefinition_NeverCarriesTheSecretAdditionalInfoValue()
    {
        var project = Project(
            resources: [new ProjectResource("docs/CONVENTIONS.md", ProjectResourceRole.Reference) { Label = "Conventions" }],
            additionalInfo: [new ProjectInfoField("Production DB password", SecretValue) { IsSecret = true }]);
        var source = FakeSource(SharedProjectPublishResult.Success("depot:payroll-processor"), out var sent);
        var viewModel = ShareProjectDialogViewModel.Create(project, [source]);
        viewModel.SelectedTarget = new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner");

        await viewModel.ShareCommand.ExecuteAsync(null);

        var definition = sent();
        Assert.DoesNotContain(SecretValue, definition.Name, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, definition.Description ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, definition.BehaviorPrompt ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, definition.GitUrl ?? "", StringComparison.Ordinal);
        Assert.All(definition.Resources, resource =>
        {
            Assert.DoesNotContain(SecretValue, resource.Reference, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, resource.Label ?? "", StringComparison.Ordinal);
        });
    }

    // AC-763: the logo now reaches PublishAsync too — read straight off disk, since Project.LogoPath already names
    // the cockpit's own stored copy by the time a project can be shared at all.
    [Fact]
    public async Task ShareAsync_ProjectHasAStoredLogo_ReadsItIntoThePublishedDefinition()
    {
        var logoPath = Path.GetTempFileName();
        var bytes = new byte[] { 137, 80, 78, 71 };
        try
        {
            await File.WriteAllBytesAsync(logoPath, bytes);
            var project = Project() with { LogoPath = logoPath };
            var source = FakeSource(SharedProjectPublishResult.Success("depot:payroll-processor"), out var sent);
            var viewModel = ShareProjectDialogViewModel.Create(project, [source]);
            viewModel.SelectedTarget = new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner");

            await viewModel.ShareCommand.ExecuteAsync(null);

            Assert.Equal(bytes, sent().LogoBytes);
        }
        finally
        {
            File.Delete(logoPath);
        }
    }

    [Fact]
    public async Task ShareAsync_ProjectHasNoLogo_SendsNoLogoBytes()
    {
        var source = FakeSource(SharedProjectPublishResult.Success("depot:payroll-processor"), out var sent);
        var viewModel = ShareProjectDialogViewModel.Create(Project(), [source]);
        viewModel.SelectedTarget = new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner");

        await viewModel.ShareCommand.ExecuteAsync(null);

        Assert.Null(sent().LogoBytes);
    }

    // IL#9: measured against the rendered markup, not the view model alone — a binding typo could pass every
    // view-model test above while the operator sees an empty column, the same discipline
    // ProjectDialogOwnershipBadgeTests already documents.
    [Fact]
    public void Render_BothColumns_ShowTheirFieldRowsAndNoSecretValueAnywhereInTheTree()
    {
        var project = Project(
            resources: [new ProjectResource("docs/CONVENTIONS.md", ProjectResourceRole.Reference) { Label = "Conventions" }],
            additionalInfo: [new ProjectInfoField("Production DB password", SecretValue) { IsSecret = true }]);
        var viewModel = ShareProjectDialogViewModel.Create(project, []);

        HeadlessAvalonia.Run(() =>
        {
            var window = new ShareProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible)
                .Select(text => text.Text)
                .ToList();
            window.Close();

            Assert.Contains("PayrollProcessor", texts);
            Assert.Contains("docs/CONVENTIONS.md", texts);
            Assert.Contains("/home/raymond/RiderProjects/payroll", texts);
            Assert.DoesNotContain(texts, text => text is { Length: > 0 } value && value.Contains(SecretValue, StringComparison.Ordinal));
        });
    }
}
