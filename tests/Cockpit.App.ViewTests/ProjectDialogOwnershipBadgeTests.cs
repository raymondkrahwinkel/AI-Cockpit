using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A field claimed read-only (AC-604/AC-247) stays locked in the real project editor, measured against the actual
/// markup rather than the view model alone.
/// </summary>
[Collection("avalonia")]
public class ProjectDialogOwnershipBadgeTests
{
    private static ISessionProfileStore ProfileStore()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([]));
        return store;
    }

    private static IMcpServerCatalog Catalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>([]));
        return catalog;
    }

    private static async Task<ProjectDialogViewModel> ViewModelAsync(
        Project project, IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? fieldOwnership = null) =>
        await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), fieldOwnership: fieldOwnership);

    [Fact]
    public async Task AReadOnlyClaimedField_TheRealTextBoxIsStillDisabled()
    {
        // IsEditable: false is the pre-AC-247 shape — nowhere to write an edit back to, so the control must stay locked.
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Behavior] = new ProjectFieldOwnership("EVE Workbench — Team", IsEditable: false),
        };
        var viewModel = await ViewModelAsync(Project.Create("Cockpit"), fieldOwnership);

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var behaviorBox = window.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(box => box.PlaceholderText != null
                    && box.PlaceholderText.Contains("Follow the project conventions"));
            window.Close();

            Assert.NotNull(behaviorBox);
            Assert.False(behaviorBox.IsEnabled, "a read-only claim has nowhere to write an edit back to, so the control must stay locked");
        });
    }
}
