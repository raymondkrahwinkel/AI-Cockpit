using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// The four fields AC-410 added to <see cref="WorkspacePaneEntry"/> (Title, NameIsChosen, SessionKind, ProjectId):
/// their round trip through <see cref="WorkspacePaneEntry.FromDomain"/>/<see cref="WorkspacePaneEntry.ToDomain"/>,
/// and that an unrecognised <c>SessionKind</c> string degrades to <see cref="PaneSessionKind.Sdk"/> the same way an
/// unrecognised <c>Kind</c> already degrades to <see cref="PaneKind.AiSession"/> — never a throw.
/// </summary>
public class WorkspacePaneEntryAiSessionTests
{
    [Fact]
    public void FromDomainThenToDomain_RoundTripsTheNewAiSessionFields()
    {
        var pane = new WorkspacePane("p1", PaneKind.AiSession)
        {
            ProfileId = "work",
            WorkingDirectory = "/home/raymond/webshop",
            Title = "webshop",
            NameIsChosen = true,
            SessionKind = PaneSessionKind.Tty,
            ProjectId = "proj-1",
        };

        var restored = WorkspacePaneEntry.FromDomain(pane).ToDomain();

        Assert.Equal(pane.Title, restored.Title);
        Assert.Equal(pane.NameIsChosen, restored.NameIsChosen);
        Assert.Equal(pane.SessionKind, restored.SessionKind);
        Assert.Equal(pane.ProjectId, restored.ProjectId);
    }

    [Fact]
    public void FromDomain_DefaultsToSdkSessionKind_WhenTheDomainPaneNeverSetIt()
    {
        var pane = new WorkspacePane("p1", PaneKind.AiSession);

        var entry = WorkspacePaneEntry.FromDomain(pane);

        Assert.Equal(nameof(PaneSessionKind.Sdk), entry.SessionKind);
    }

    // One behaviour, one test: ToDomain reads the stored string through a single case-insensitive TryParse and
    // falls back to Sdk on anything it does not recognise — a future kind, a blank, or a differently cased name.
    [Theory]
    [InlineData("some-future-kind", PaneSessionKind.Sdk)]
    [InlineData("", PaneSessionKind.Sdk)]
    [InlineData("tty", PaneSessionKind.Tty)]
    public void ToDomain_ReadsTheSessionKindString_FallingBackToSdkWhenItIsNotOne(string stored, PaneSessionKind expected)
    {
        var entry = new WorkspacePaneEntry { Id = "p1", Kind = nameof(PaneKind.AiSession), SessionKind = stored };

        var pane = entry.ToDomain();

        Assert.Equal(expected, pane.SessionKind);
    }
}
