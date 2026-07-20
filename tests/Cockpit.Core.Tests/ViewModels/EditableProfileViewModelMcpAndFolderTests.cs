using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using FluentAssertions;

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
    public void Load_SeedsTheDefaultWorkingDirectory_AndRoundTripsIt()
    {
        var editable = new EditableProfileViewModel(ClaudeProfile(defaultWorkingDirectory: "/home/r/App"), isLoggedIn: true);

        editable.DefaultWorkingDirectory.Should().Be("/home/r/App");
        editable.ToProfile().DefaultWorkingDirectory.Should().Be("/home/r/App");
    }

    [Fact]
    public void Save_CollapsesABlankDefaultWorkingDirectoryToNull()
    {
        var editable = new EditableProfileViewModel(ClaudeProfile(), isLoggedIn: true) { DefaultWorkingDirectory = "   " };

        editable.ToProfile().DefaultWorkingDirectory.Should().BeNull();
    }

    [Fact]
    public void Load_TicksEachAvailableServer_WhenTheProfileHasNoRestriction()
    {
        var editable = new EditableProfileViewModel(
            ClaudeProfile(), isLoggedIn: true, availableMcpServerNames: ["youtrack", "docker"]);

        editable.RestrictMcpServers.Should().BeFalse();
        editable.HasMcpServers.Should().BeTrue();
        editable.McpServers.Should().OnlyContain(server => server.IsEnabledForSession);
    }

    [Fact]
    public void Load_TicksOnlyTheSavedServers_WhenTheProfileRestricts()
    {
        var editable = new EditableProfileViewModel(
            ClaudeProfile(enabledMcpServerNames: ["docker"]), isLoggedIn: true, availableMcpServerNames: ["youtrack", "docker"]);

        editable.RestrictMcpServers.Should().BeTrue();
        editable.McpServers.Single(server => server.Name == "youtrack").IsEnabledForSession.Should().BeFalse();
        editable.McpServers.Single(server => server.Name == "docker").IsEnabledForSession.Should().BeTrue();
    }

    [Fact]
    public void Save_WithTheGateOff_PersistsNoRestriction()
    {
        var editable = new EditableProfileViewModel(
            ClaudeProfile(), isLoggedIn: true, availableMcpServerNames: ["youtrack", "docker"]);

        editable.ToProfile().EnabledMcpServerNames.Should().BeNull();
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

        editable.ToProfile().EnabledMcpServerNames.Should().Equal("youtrack");
    }

    [Fact]
    public void Save_PreservesASavedServerTheCatalogDidNotOffer_SoAbsenceDoesNotSilentlyDropIt()
    {
        // "youtrack" is in the profile's saved selection but not currently in the catalog (disabled / plugin not
        // loaded), so it has no row to tick. Saving the profile — even editing only the visible "docker" — must not
        // forget it.
        var editable = new EditableProfileViewModel(
            ClaudeProfile(enabledMcpServerNames: ["youtrack", "docker"]), isLoggedIn: true, availableMcpServerNames: ["docker"]);

        editable.ToProfile().EnabledMcpServerNames.Should().BeEquivalentTo("youtrack", "docker");
    }

    [Fact]
    public void Save_WithNoCatalogAtAll_KeepsTheEntireSavedSelection()
    {
        var editable = new EditableProfileViewModel(
            ClaudeProfile(enabledMcpServerNames: ["youtrack", "docker"]), isLoggedIn: true, availableMcpServerNames: null);

        editable.ToProfile().EnabledMcpServerNames.Should().BeEquivalentTo("youtrack", "docker");
    }
}
