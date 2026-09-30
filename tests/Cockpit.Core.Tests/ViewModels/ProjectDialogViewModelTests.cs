using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

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
        // AC-766: CreateAsync now reads GetServersForProjectAsync(project?.Id) — stubbed for any id (including
        // null) so every existing test, written against the project-agnostic call, keeps seeing the same servers.
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(servers);
        return catalog;
    }

    // AC-618: category — a plain, never-claimed field on the editor.

    // AC-618: chips under the Category field — the known categories the host (SessionDialogService) hands
    // CreateAsync, offered as a click instead of retyping.

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

    // --- AC-604: project-field ownership --------------------------------------------------------------------

    // AC-938: the extra repository rows below the Folder box — repo #2 and on.

}
