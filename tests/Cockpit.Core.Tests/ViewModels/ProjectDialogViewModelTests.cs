using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The project editor (AC-160): what it opens with, and what it hands back on Save. The overlay it produces is
/// what a project's sessions actually get, so an unticked row that fails to reach the saved project is a server
/// silently still on.
/// </summary>
public class ProjectDialogViewModelTests
{
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
}
