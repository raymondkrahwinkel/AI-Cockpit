using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectRolesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Admin")]
    [InlineData("EditorX")]
    public void TryParse_UnrecognizedText_ReturnsNullRatherThanGuessing(string? text)
    {
        Assert.Null(CockpitProjectRoles.TryParse(text));
    }

    [Theory]
    [InlineData(CockpitProjectRole.Editor, true)]
    [InlineData(CockpitProjectRole.Owner, true)]
    [InlineData(CockpitProjectRole.Viewer, false)]
    public void CanWrite_MirrorsDepotsOwnEditorMinimum(CockpitProjectRole role, bool expected)
    {
        Assert.Equal(expected, role.CanWrite());
    }
}
