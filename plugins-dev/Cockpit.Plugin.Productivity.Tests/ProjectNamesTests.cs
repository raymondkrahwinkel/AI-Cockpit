using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Productivity.Tests;

// AC-1766 layer 2 (data integrity): the project a stored value is shown and booked as.
public sealed class ProjectNamesTests
{
    [Theory]
    [InlineData(@"C:\Users\raymo\AppData\Roaming\Cockpit\worktrees\308c2a9b28e1\cockpit-personal-accde346-accde346", "Cockpit", null)]
    [InlineData("3650ac1d0f2b4c6e9a7d1e5f8b2c4a60", "Onbekend project (3650ac1d…)", "'3650ac1d0f2b4c6e9a7d1e5f8b2c4a60' hoort bij geen project in de cockpit: het project is weg, of de map is van vóór de projecten.")]
    public void Resolve_FindsAWorktreeFoldersProjectThroughItsSession_AndNamesAnUnknownIdAsUnknown(string stored, string expectedLabel, string? expectedTooltip)
    {
        const string cockpit = "b9f48b5c01694695b1e7f0714ca06ef1";
        var names = new ProjectNames(
            [new PluginProjectInfo(cockpit, "Cockpit"), new PluginProjectInfo("7a63289f0d6d4870a8d98387dbbeb621", "Depot")],
            [new SessionLabel("accde34607254a208ad3f985ece5fe8f", cockpit, "AC-1766", "AC-1766", "personal", "opus", true)]);

        var name = names.Resolve(stored);

        Assert.Equal(expectedLabel, name.Label);
        Assert.Equal(expectedTooltip, name.Tooltip);
        Assert.Equal(expectedTooltip is null ? cockpit : null, name.ProjectId);
    }
}
