using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The AC-130 arm of <see cref="EditableProfileViewModel"/>: a profile's default working directory and its MCP-server
/// pre-selection must survive the round-trip through the profile editor, so a project profile actually remembers the
/// folder and servers a new session should start with. The gate (<see cref="EditableProfileViewModel.RestrictMcpServers"/>)
/// is what tells "all servers, future ones included" (null) apart from an explicit chosen set.
/// </summary>
public class EditableProfileViewModelMcpAndFolderTests
{
    private static SessionProfile ClaudeProfile(
        string? defaultWorkingDirectory = null,
        IReadOnlyList<string>? enabledMcpServerNames = null) =>
        new("work", ClaudePluginProfile.Create("/home/r/.claude-work", null))
        {
            DefaultWorkingDirectory = defaultWorkingDirectory,
            EnabledMcpServerNames = enabledMcpServerNames,
        };

    [Fact]
    public void Save_WithTheGateOff_PersistsNoRestriction()
    {
        var editable = new EditableProfileViewModel(
            ClaudeProfile(), isLoggedIn: true, availableMcpServerNames: ["youtrack", "docker"]);

        Assert.Null(editable.ToProfile().EnabledMcpServerNames);
    }

    [Fact]
    public void Save_WithTheGateOn_PersistsExactlyTheTickedServers()
    {
        var editable = new EditableProfileViewModel(
            ClaudeProfile(), isLoggedIn: true, availableMcpServerNames: ["youtrack", "docker"])
        {
            RestrictMcpServers = true,
        };
        editable.McpServers.Single(server => server.Name == "docker").IsEnabledForSession = false;

        Assert.Equal(new[] { "youtrack" }, editable.ToProfile().EnabledMcpServerNames);
    }

}
