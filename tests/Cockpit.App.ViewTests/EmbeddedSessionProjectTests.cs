using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The host giving an embedded session its project (AC-320): a plugin that embeds a session names a folder and
/// nothing else, so the host works out which project that is and puts it on the session — before the start, where
/// a plugin's per-project contribution to the launch (AC-165) can still read it.
/// <para>
/// Here rather than in the unit tests because the view model has to be built on the UI thread; the rule it applies
/// is <c>EmbeddedSessionProject</c>, which is proven on its own.
/// </para>
/// </summary>
[Collection("avalonia")]
public class EmbeddedSessionProjectTests
{
    [Fact]
    public async Task ApplyEmbeddedProject_PlacesTheRunOnTheProjectThatOwnsItsFolder()
    {
        var (cockpit, session) = _CockpitOwning("/repos/cockpit");

        await cockpit._ApplyEmbeddedProjectAsync(session, new EmbeddedSessionRequest { WorkingDirectory = "/repos/cockpit" });

        session.ProjectId.Should().Be("cockpit");
    }

    [Fact]
    public async Task ApplyEmbeddedProject_AFolderNoProjectOwns_LeavesTheRunWithoutOne()
    {
        var (cockpit, session) = _CockpitOwning("/repos/cockpit");

        await cockpit._ApplyEmbeddedProjectAsync(session, new EmbeddedSessionRequest { WorkingDirectory = "/tmp/scratch" });

        session.ProjectId.Should().BeNull();
    }

    [Fact]
    public async Task ApplyEmbeddedProject_WithoutAWorkingDirectory_AsksNothing()
    {
        var (cockpit, session) = _CockpitOwning("/repos/cockpit");

        await cockpit._ApplyEmbeddedProjectAsync(session, new EmbeddedSessionRequest());

        session.ProjectId.Should().BeNull();
    }

    // A cockpit holding one project on the given folder, and a fresh session to place. Built on the UI thread, which
    // is where the session collections and the projects list live.
    private static (CockpitViewModel Cockpit, SessionViewModel Session) _CockpitOwning(string sourceDirectory) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            cockpit.Projects.Projects.Add(new Project("cockpit", "Cockpit") { SourceDirectory = sourceDirectory });
            return (cockpit, new SessionViewModel());
        });
}
