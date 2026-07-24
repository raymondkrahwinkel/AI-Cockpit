using FluentAssertions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The precedence rule between a project and a profile (Raymond, 2026-07-24): a project is an override on top of
/// a profile — where both answer, the project wins; where it stays silent, the profile's default stands. Pinned
/// here because the same question is asked from the dialog, the launcher and the sidebar, and three copies of the
/// rule would eventually disagree.
/// </summary>
public class SessionStartDefaultsTests
{
    private static SessionProfile Profile(string label = "personal") =>
        new(label, new ClaudeConfig("~/.claude-personal"));

    [Fact]
    public void Resolve_NoProject_UsesTheProfileDefaults()
    {
        var profile = Profile() with { DefaultWorkingDirectory = "/home/raymond/profile-dir" };

        var defaults = SessionStartDefaults.Resolve(project: null, profile);

        defaults.WorkingDirectory.Should().Be("/home/raymond/profile-dir");
        defaults.ProfileLabel.Should().Be("personal");
        defaults.IsolateInWorktree.Should().BeFalse();
    }

    [Fact]
    public void Resolve_ProjectWithASourceDirectory_OverridesTheProfileDefault()
    {
        var profile = Profile() with { DefaultWorkingDirectory = "/home/raymond/profile-dir" };
        var project = Project.Create("Cockpit") with { SourceDirectory = "/home/raymond/RiderProjects/AI-Cockpit" };

        SessionStartDefaults.Resolve(project, profile).WorkingDirectory
            .Should().Be("/home/raymond/RiderProjects/AI-Cockpit");
    }

    [Fact]
    public void Resolve_ProjectWithoutASourceDirectory_FallsBackToTheProfile()
    {
        var profile = Profile() with { DefaultWorkingDirectory = "/home/raymond/profile-dir" };
        var project = Project.Create("Admin");

        SessionStartDefaults.Resolve(project, profile).WorkingDirectory.Should().Be("/home/raymond/profile-dir");
    }

    [Fact]
    public void Resolve_NeitherNamesAFolder_FallsBackToTheGlobalDefault()
    {
        SessionStartDefaults.Resolve(Project.Create("Admin"), Profile(), "/home/raymond")
            .WorkingDirectory.Should().Be("/home/raymond");
    }

    [Fact]
    public void Resolve_BlankProjectFolder_CountsAsUnset()
    {
        var profile = Profile() with { DefaultWorkingDirectory = "/home/raymond/profile-dir" };
        var project = Project.Create("Admin") with { SourceDirectory = "   " };

        SessionStartDefaults.Resolve(project, profile).WorkingDirectory.Should().Be("/home/raymond/profile-dir");
    }

    [Fact]
    public void Resolve_ProjectNamingAProfile_PreselectsThatOneOverTheCurrentSelection()
    {
        var project = Project.Create("Work") with { DefaultProfileLabel = "work" };

        SessionStartDefaults.Resolve(project, Profile()).ProfileLabel.Should().Be("work");
    }

    [Fact]
    public void Resolve_ProjectIsolatingByDefault_PreselectsTheWorktreeChoice()
    {
        var project = Project.Create("Cockpit") with { IsolateInWorktreeByDefault = true };

        SessionStartDefaults.Resolve(project, Profile()).IsolateInWorktree.Should().BeTrue();
    }

    /// <summary>
    /// The MCP selection stays the profile's: the project's overlay decides which servers <em>exist</em> for its
    /// sessions, this list decides which of the offered ones open ticked. Two lists that could contradict each
    /// other is exactly what the single-resolver rule is there to prevent.
    /// </summary>
    [Fact]
    public void Resolve_McpSelection_ComesFromTheProfileNotTheProjectOverlay()
    {
        var profile = Profile() with { EnabledMcpServerNames = ["youtrack"] };
        var project = Project.Create("Cockpit") with
        {
            McpOverlay = new ProjectMcpOverlay { DisabledServerNames = ["depot"] },
        };

        SessionStartDefaults.Resolve(project, profile).EnabledMcpServerNames.Should().Equal("youtrack");
    }

    /// <summary>
    /// The profile says who the session is (AC-142: "You are Olaf; your memory is in the Depot MCP"), the project
    /// what it is working on. Both apply, identity first — the project appends, it does not replace.
    /// </summary>
    [Fact]
    public void Resolve_BothCarryInstructions_AppendsTheProjectsUnderTheProfiles()
    {
        var profile = Profile() with { SystemPrompt = "You are Olaf. Look yourself up in the Depot MCP." };
        var project = Project.Create("Cockpit") with { BehaviorPrompt = "Test before opening a PR." };

        SessionStartDefaults.Resolve(project, profile).SystemPrompt
            .Should().Be("You are Olaf. Look yourself up in the Depot MCP.\n\nTest before opening a PR.");
    }

    [Fact]
    public void Resolve_OnlyTheProfileSpeaks_UsesItAlone()
    {
        var profile = Profile() with { SystemPrompt = "You are Olaf." };

        SessionStartDefaults.Resolve(Project.Create("Cockpit"), profile).SystemPrompt.Should().Be("You are Olaf.");
    }

    [Fact]
    public void Resolve_OnlyTheProjectSpeaks_UsesItAlone()
    {
        var project = Project.Create("Cockpit") with { BehaviorPrompt = "Test before opening a PR." };

        SessionStartDefaults.Resolve(project, Profile()).SystemPrompt.Should().Be("Test before opening a PR.");
    }

    [Fact]
    public void Resolve_NeitherSpeaks_AppendsNothing()
    {
        SessionStartDefaults.Resolve(Project.Create("Cockpit"), Profile()).SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void Resolve_BlankInstructions_CountAsUnset()
    {
        var profile = Profile() with { SystemPrompt = "   " };
        var project = Project.Create("Cockpit") with { BehaviorPrompt = "\n" };

        SessionStartDefaults.Resolve(project, profile).SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void Resolve_NoProfile_LeavesEveryProfileBackedFieldAlone()
    {
        var project = Project.Create("Cockpit") with { SourceDirectory = "/src" };

        var defaults = SessionStartDefaults.Resolve(project, profile: null);

        defaults.WorkingDirectory.Should().Be("/src");
        defaults.ProfileLabel.Should().BeNull();
        defaults.EnabledMcpServerNames.Should().BeNull();
    }

    [Fact]
    public void Resolve_AProjectWithAMemoryLocation_TellsTheSessionWhereToLook()
    {
        var project = Project.Create("Cockpit") with
        {
            BehaviorPrompt = "Work ticket by ticket.",
            MemoryRef = "/home/raymond/Notes/Cockpit",
        };
        var profile = new SessionProfile("work", new ClaudeConfig("~/.claude")) { SystemPrompt = "You are Olaf." };

        var defaults = SessionStartDefaults.Resolve(project, profile);

        // Told, not loaded: the host does not know what lives there — a folder of notes, a Depot project — and a
        // session that is told where to look can go and look.
        defaults.SystemPrompt.Should().Be(
            "You are Olaf.\n\nWork ticket by ticket.\n\nThis project's memory lives at /home/raymond/Notes/Cockpit. " +
            "Read it there when you need what this project already knows, and keep it up to date as you work.");
    }

    [Fact]
    public void Resolve_AProjectWithoutOne_SaysNothingAboutMemory()
    {
        var defaults = SessionStartDefaults.Resolve(Project.Create("Cockpit"), new SessionProfile("work", new ClaudeConfig("~/.claude")));

        defaults.SystemPrompt.Should().BeNull();
    }
}
