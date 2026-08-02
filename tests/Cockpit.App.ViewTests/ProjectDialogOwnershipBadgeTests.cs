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
/// The origin badges in the real project editor (AC-604) — measured against the actual markup, not the view model
/// alone, because a binding typo (the wrong path, a missing <c>IsVisible</c>) passes a view-model-only test happily
/// while the operator sees nothing or the wrong thing. Covers the two review findings on the first render: Folder
/// and Profile must carry the fixed "This machine" badge whenever a project has any claim at all (a badge-less
/// field must not exist, review point 2), and the MCP-overlay row — the one part of the dialog the screenshot
/// itself could not confirm, since it falls below <c>DialogScreenClamp</c>'s fold — is measured here directly
/// against the visual tree instead, which is not clamped the way a screenshot's viewport is.
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

    /// <summary>
    /// Every <em>actually shown</em> badge's text — a hidden control is still in the visual tree (an unset
    /// <c>IsVisible</c> only stops it painting), so this filters on <c>IsEffectivelyVisible</c> the
    /// same way <c>ProjectDialogPluginFieldTests.VisibleHeading</c> already has to. Not scoped to the viewport: a
    /// plain StackPanel-in-ScrollViewer does not virtualize, so a row scrolled out of sight is still found here.
    /// </summary>
    private static List<string?> BadgeTexts(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("originBadge") && border.IsEffectivelyVisible)
            .Select(border => (border.Child as TextBlock)?.Text)];

    /// <summary>
    /// The <em>shown</em> badge sitting beside <paramref name="labelText"/>'s own field label — the direct sibling
    /// in its label row, not just "a badge somewhere in the window" (which several rows carry at once and would
    /// not say which field a match belongs to, the exact "matches an ancestor, not the field itself" trap).
    /// </summary>
    private static string? BadgeNextToLabel(Window window, string labelText)
    {
        var label = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("FieldLabel") && text.Text == labelText);
        var row = label?.Parent as StackPanel;
        var badge = row?.Children.OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("originBadge") && border.IsEffectivelyVisible);
        return (badge?.Child as TextBlock)?.Text;
    }

    [Fact]
    public async Task AnUnclaimedProject_ShowsNoBadgeAnywhere()
    {
        var viewModel = await ViewModelAsync(Project.Create("Cockpit"));

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var badges = BadgeTexts(window);
            window.Close();

            Assert.Empty(badges);
        });
    }

    [Fact]
    public async Task AClaimedProject_BadgesFolderAndProfileAsThisMachine()
    {
        // Folder and Profile are not among the six claimable host fields — a checkout path and a local profile
        // pick are always this machine's — but review point 2 was that their absence read as "unknown" next to
        // rows that do carry a badge. Fixed: both carry "This machine" whenever the project has any claim.
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Name] = new ProjectFieldOwnership("Depot — Work"),
        };
        var viewModel = await ViewModelAsync(Project.Create("Cockpit"), fieldOwnership);

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var folderBadge = BadgeNextToLabel(window, "Folder");
            var profileBadge = BadgeNextToLabel(window, "Profile");
            window.Close();

            Assert.Equal("● This machine", folderBadge);
            Assert.Equal("● This machine", profileBadge);
        });
    }

    [Fact]
    public async Task AClaimedProject_TheMcpOverlayRowCarriesItsOwnBadge()
    {
        // Point 3: not "identical markup, so it must render the same" as a claim — measured directly against the
        // visual tree, which a screenshot's DialogScreenClamp-limited viewport could not confirm.
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.McpOverlay] = new ProjectFieldOwnership("Depot — Work"),
        };
        var viewModel = await ViewModelAsync(Project.Create("Cockpit"), fieldOwnership);

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var mcpOverlayBadge = BadgeNextToLabel(window, "MCP overlay");
            window.Close();

            Assert.Equal("◆ Shared", mcpOverlayBadge);
        });
    }

    [Fact]
    public async Task AnEditableClaimedField_TheRealTextBoxIsStillDisabled()
    {
        // Review point 1, measured against the actual control rather than the view model: IsEditable: true must
        // not leave the real TextBox enabled while there is nowhere for an edit to go.
        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Behavior] = new ProjectFieldOwnership("Depot — Work", IsEditable: true),
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
            Assert.False(behaviorBox.IsEnabled, "an editable claim has nowhere to write an edit back to yet, so the control must stay locked");
        });
    }
}
