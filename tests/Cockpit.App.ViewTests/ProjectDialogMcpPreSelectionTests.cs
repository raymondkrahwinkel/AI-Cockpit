using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// What the project editor says about its MCP pre-selection without being expanded, measured against the real
/// markup. Three Codex sessions ran for months on a project whose Depot server was switched off, because the one
/// line a collapsed checklist showed was a count — true, and silent about the only thing that mattered. A
/// view-model-only test would not have caught it: the state existed, nothing put it on screen.
/// </summary>
[Collection("avalonia")]
public class ProjectDialogMcpPreSelectionTests
{
    private static ISessionProfileStore ProfileStore()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([]));
        return store;
    }

    private static IMcpServerCatalog Catalog(params McpServerConfig[] servers)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>(servers));
        return catalog;
    }

    private static McpServerConfig Server(string name) => new() { Name = name, Command = "npx" };

    /// <summary>The collapsed checklist's own header line, as it is actually painted.</summary>
    private static string? CollapsedSummary(Window window) =>
        window.GetVisualDescendants().OfType<ToggleButton>()
            .Where(toggle => toggle.Classes.Contains("Collapser") && toggle.IsEffectivelyVisible)
            .SelectMany(toggle => toggle.GetVisualDescendants().OfType<TextBlock>())
            .Select(text => text.Text)
            .FirstOrDefault(text => text is not null && text.StartsWith("MCP servers", StringComparison.Ordinal));

    private static string? Summarise(ProjectDialogViewModel viewModel)
    {
        string? summary = null;
        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();
            summary = CollapsedSummary(window);
            window.Close();
        });
        return summary;
    }

    /// <summary>
    /// EVE Together's case: one server switched off, and the operator has to be able to see which without
    /// expanding anything. "13 of 14 selected" was the line that hid this for months.
    /// </summary>
    [Fact]
    public async Task ASwitchedOffServer_IsNamedInTheCollapsedSummary()
    {
        var project = Project.Create("EVE Together") with
        {
            McpOverlay = new ProjectMcpOverlay { DisabledServerNames = ["Depot: krahwinkel-it.nl"] },
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(Server("Depot: krahwinkel-it.nl"), Server("youtrack"), Server("cockpit-verify")));

        Assert.Equal("MCP servers · Depot: krahwinkel-it.nl off", Summarise(viewModel));
    }

    /// <summary>
    /// And the state itself: a project running a saved list says so on a control the operator sees without
    /// expanding, which is the half of this a per-row summary cannot state.
    /// </summary>
    [Fact]
    public async Task AProjectWithASavedPreSelection_ShowsTheGateTicked()
    {
        var project = Project.Create("EVE Together") with
        {
            McpOverlay = new ProjectMcpOverlay { EnabledServerNames = ["youtrack"] },
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(Server("youtrack"), Server("registered-later")));

        bool? gate = null;
        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            gate = window.GetVisualDescendants().OfType<CheckBox>()
                .Where(box => box.IsEffectivelyVisible)
                .FirstOrDefault(box => box.Content as string == "Pre-select MCP servers for this project")
                ?.IsChecked;
            window.Close();
        });

        Assert.True(gate);
    }

    /// <summary>
    /// AC-248: a shared definition naming a server this machine has not got is painted, not merely carried. It used
    /// to be kept in `_carriedEnabledServerNames` and written straight back on save with nothing on screen — the
    /// silent mismatch Raymond called worse than a visible one on 2026-08-02. Read off the real markup because the
    /// checklist around it is disabled for a Viewer, which is where an unpainted-looking warning would hide.
    /// </summary>
    [Fact]
    public async Task AServerThisMachineHasNot_IsPaintedEvenWhileTheChecklistIsLocked()
    {
        var project = Project.Create("EVE Together") with
        {
            McpOverlay = new ProjectMcpOverlay { EnabledServerNames = ["youtrack", "playwright"] },
        };
        var ownership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.McpOverlay] = new ProjectFieldOwnership("Depot — Work", IsEditable: false),
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(Server("youtrack")), fieldOwnership: ownership);

        string? warning = null;
        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            warning = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible)
                .Select(text => text.Text)
                .FirstOrDefault(text => text is not null && text.StartsWith("Not on this machine", StringComparison.Ordinal));
            window.Close();
        });

        Assert.Equal("Not on this machine: playwright — a session starts without it.", warning);
    }
}
