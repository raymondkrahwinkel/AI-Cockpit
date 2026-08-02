using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectRolesTests
{
    [Theory]
    [InlineData("Viewer", CockpitProjectRole.Viewer)]
    [InlineData("viewer", CockpitProjectRole.Viewer)]
    [InlineData("VIEWER", CockpitProjectRole.Viewer)]
    [InlineData("Editor", CockpitProjectRole.Editor)]
    [InlineData("Owner", CockpitProjectRole.Owner)]
    [InlineData("  Owner  ", CockpitProjectRole.Owner)]
    public void TryParse_KnownRoleText_ParsesCaseInsensitively(string text, CockpitProjectRole expected)
    {
        Assert.Equal(expected, CockpitProjectRoles.TryParse(text));
    }

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

    [Fact]
    public void WriteDeniedReason_IsNeverBlank()
    {
        var reason = CockpitProjectRole.Viewer.WriteDeniedReason();

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("Viewer", reason, StringComparison.Ordinal);
    }
}
