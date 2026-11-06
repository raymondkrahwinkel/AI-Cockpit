using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
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

    private static ShareProjectDialogViewModel _Create(Project project, IReadOnlyList<ISharedProjectSource> sources) =>
        ShareProjectDialogViewModel.Create(project, sources, new SharedProjectDefinitionBuilder());

    private static Project Project(IReadOnlyList<ProjectResource>? resources = null, IReadOnlyList<ProjectInfoField>? additionalInfo = null) =>
        Core.Projects.Project.Create("PayrollProcessor") with
        {
            Description = "Loonverwerking",
            GitUrl = "git@github.com:example/payroll.git",
            SourceDirectories = [new("/home/raymond/RiderProjects/payroll")],
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
    public void Rows_SecretShapedResourceReference_StaysOnThisMachineAndNeverShowsTheReferenceItself()
    {
        var project = Project(resources: [new ProjectResource("~/.ssh/id_rsa", ProjectResourceRole.Reference) { Label = "Deploy key" }]);
        var viewModel = _Create(project, []);

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
        var viewModel = _Create(project, []);

        Assert.DoesNotContain(viewModel.GoesToDepot, row => row.Label.Contains("Repository") || row.Value.Contains(SecretValue));
        Assert.DoesNotContain(viewModel.StaysOnThisMachine, row => row.Value.Contains(SecretValue));
    }

    // AC-762 bijvangst: sharing an already-shared project used to prepend a second Memory row instead of replacing
    // the first, so "Stop sharing" (which removes only the first match) left the stale row's binding intact.
    [Fact]
    public async Task ShareAsync_Success_AProjectAlreadySharedOnce_ReplacesTheExistingMemoryRowRatherThanStacking()
    {
        var project = Project(resources: [new ProjectResource("depot:payroll-processor-old", ProjectResourceRole.Memory)]);
        var source = FakeSource(SharedProjectPublishResult.Success("depot:payroll-processor"), out _);
        var viewModel = _Create(project, [source]);

        Project? closed = null;
        viewModel.CloseRequested += result => closed = result;
        viewModel.SelectedTarget = new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner");

        await viewModel.ShareCommand.ExecuteAsync(null);

        Assert.NotNull(closed);
        var binding = Assert.Single(closed!.Resources);
        Assert.Equal(ProjectResourceRole.Memory, binding.Role);
        Assert.Equal("depot:payroll-processor", binding.Reference);
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
        var viewModel = _Create(project, [source]);
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
}
