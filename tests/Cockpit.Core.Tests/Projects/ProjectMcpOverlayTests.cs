using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// The variant-B merge (AC-159): the global registry is the base, and a project turns servers off, adds its own,
/// or overrides one by name. What a project's sessions actually get to talk to, so a wrong answer here is a
/// server a session silently does or does not have.
/// </summary>
public class ProjectMcpOverlayTests
{
    private static readonly IReadOnlyList<McpServerConfig> Registry =
    [
        new() { Name = "youtrack", Command = "npx" },
        new() { Name = "depot", Url = "https://depot.example/mcp" },
    ];

    [Fact]
    public void ApplyTo_EmptyOverlay_ReturnsTheRegistryUntouched()
    {
        Assert.Same(Registry, ProjectMcpOverlay.None.ApplyTo(Registry));
    }

    [Fact]
    public void ApplyTo_ADisabledName_StillOffersThatServer()
    {
        // A project narrows what is selected, never what is offered (Raymond, 2026-07-24): the checklist lists every
        // server whichever project is picked, and switching one off shows as an unticked box the operator can undo.
        var overlay = new ProjectMcpOverlay { DisabledServerNames = ["youtrack"] };

        Assert.Equivalent(new object[] { "depot", "youtrack" }, overlay.ApplyTo(Registry).Select(server => server.Name));
    }

    /// <summary>
    /// Why the choice is stored as a list of what is on (Raymond, 2026-08-01): a server added to the registry after
    /// the project made its choice is in nobody's list, so a project that narrowed its servers stays narrowed. The
    /// off-list this replaced had the opposite hole — a new server was in no project's off-list, so it arrived ticked
    /// everywhere, including in the projects that had switched almost everything off.
    /// </summary>
    [Fact]
    public void IsSelectedByDefault_AServerAddedAfterTheChoice_IsUntickedForANarrowedProject()
    {
        var overlay = new ProjectMcpOverlay { EnabledServerNames = ["YouTrack"] };

        Assert.True(overlay.IsSelectedByDefault("youtrack"), "matched case-insensitively, like every other name here");
        Assert.False(overlay.IsSelectedByDefault("depot"));
        Assert.False(overlay.IsSelectedByDefault("brand-new"));
    }

    /// <summary>An empty list is a project that ticked nothing — a real answer, not a project that never answered.</summary>
    [Fact]
    public void IsSelectedByDefault_AnEmptyEnabledList_TicksNothing()
    {
        var overlay = new ProjectMcpOverlay { EnabledServerNames = [] };

        Assert.False(overlay.IsSelectedByDefault("youtrack"));
        Assert.False(overlay.IsEmpty, "a project that ticked nothing has said something, so it must survive a save");
    }

    /// <summary>A project saved by an earlier build still reads back the way it was written.</summary>
    [Fact]
    public void IsSelectedByDefault_TheOlderDisabledList_StillApplies()
    {
        var overlay = new ProjectMcpOverlay { DisabledServerNames = ["youtrack"] };

        Assert.False(overlay.IsSelectedByDefault("youtrack"));
        Assert.True(overlay.IsSelectedByDefault("depot"));
    }

    [Fact]
    public void IsSelectedByDefault_ADisabledName_IsUnticked_MatchedCaseInsensitively()
    {
        var overlay = new ProjectMcpOverlay { DisabledServerNames = ["YouTrack"] };

        Assert.False(overlay.IsSelectedByDefault("youtrack"));
        Assert.True(overlay.IsSelectedByDefault("depot"));
    }

    [Fact]
    public void IsSelectedByDefault_WithNoChoicesMade_TicksEverything()
    {
        Assert.True(ProjectMcpOverlay.None.IsSelectedByDefault("depot"));
    }

    [Fact]
    public void ApplyTo_AdditionalServer_IsAppended()
    {
        var overlay = new ProjectMcpOverlay
        {
            AdditionalServers = [new McpServerConfig { Name = "project-tools", Command = "uvx" }],
        };

        Assert.Equal(
            new[] { "youtrack", "depot", "project-tools" },
            overlay.ApplyTo(Registry).Select(server => server.Name));
    }

    [Fact]
    public void ApplyTo_AdditionalServerWithARegistryName_ReplacesItInPlace()
    {
        var overlay = new ProjectMcpOverlay
        {
            AdditionalServers = [new McpServerConfig { Name = "depot", Url = "https://project.example/mcp" }],
        };

        var effective = overlay.ApplyTo(Registry);

        Assert.Equal(2, System.Linq.Enumerable.Count(effective));
        Assert.Equal("https://project.example/mcp", effective.Single(server => server.Name == "depot").Url);
    }

    /// <summary>
    /// Switching a project-owned server off has to leave it defined and merely off — otherwise the only way to
    /// silence one would be to delete it and type it back in later.
    /// </summary>
    [Fact]
    public void ApplyTo_AServerBothAddedAndSwitchedOff_IsStillOffered_ButUnticked()
    {
        var overlay = new ProjectMcpOverlay
        {
            AdditionalServers = [new McpServerConfig { Name = "project-tools", Command = "uvx" }],
            DisabledServerNames = ["project-tools"],
        };

        Assert.Contains(overlay.ApplyTo(Registry), server => server.Name == "project-tools");
        Assert.False(overlay.IsSelectedByDefault("project-tools"));
    }

    /// <summary>A hand-edited config that lists a server twice costs the operator the duplicate, not the whole load.</summary>
    [Fact]
    public void ApplyTo_DuplicateAdditionalNames_KeepsTheFirstAndDoesNotThrow()
    {
        var overlay = new ProjectMcpOverlay
        {
            AdditionalServers =
            [
                new McpServerConfig { Name = "project-tools", Command = "first" },
                new McpServerConfig { Name = "project-tools", Command = "second" },
            ],
        };

        var effective = overlay.ApplyTo(Registry);

        var match = Assert.Single(effective, server => server.Name == "project-tools");
        Assert.Equal("first", match.Command);
    }

    [Fact]
    public void ApplyTo_AProjectServerNamedAfterADisabledRegistryOne_StaysDisabled()
    {
        // A project narrows what is offered, never widens it: a server the operator switched off globally must not
        // come back — under its familiar name, with a command of the project's choosing — because a project says so.
        var registry = new List<McpServerConfig> { new() { Name = "filesystem", Enabled = false, Command = "npx" } };
        var overlay = new ProjectMcpOverlay
        {
            AdditionalServers = [new McpServerConfig { Name = "filesystem", Enabled = true, Command = "something-else" }],
        };

        Assert.False(Assert.Single(overlay.ApplyTo(registry)).Enabled);
    }
}
