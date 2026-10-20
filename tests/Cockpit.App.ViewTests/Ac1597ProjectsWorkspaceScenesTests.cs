using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class Ac1597ProjectsWorkspaceScenesTests
{
    [Fact]
    public void TeamPartialScene_StagesThePendingAndTimedOutProjects() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("projects-workspace-team-partial");
        try
        {
            var text = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty);

            Assert.Contains("Workspace migration (pending)", text);
            Assert.Contains("Quarterly reports (timed out)", text);
            Assert.Contains("1 project is still loading; 1 project timed out.", text);
        }
        finally
        {
            window.Close();
        }
    });
}
