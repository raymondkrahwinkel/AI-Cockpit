using FluentAssertions;
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
        ProjectMcpOverlay.None.ApplyTo(Registry).Should().BeSameAs(Registry);
    }

    [Fact]
    public void ApplyTo_ADisabledName_StillOffersThatServer()
    {
        // A project narrows what is selected, never what is offered (Raymond, 2026-07-24): the checklist lists every
        // server whichever project is picked, and switching one off shows as an unticked box the operator can undo.
        var overlay = new ProjectMcpOverlay { DisabledServerNames = ["youtrack"] };

        overlay.ApplyTo(Registry).Select(server => server.Name).Should().BeEquivalentTo("depot", "youtrack");
    }

    [Fact]
    public void IsSelectedByDefault_ADisabledName_IsUnticked_MatchedCaseInsensitively()
    {
        var overlay = new ProjectMcpOverlay { DisabledServerNames = ["YouTrack"] };

        overlay.IsSelectedByDefault("youtrack").Should().BeFalse();
        overlay.IsSelectedByDefault("depot").Should().BeTrue();
    }

    [Fact]
    public void IsSelectedByDefault_WithNoChoicesMade_TicksEverything()
    {
        ProjectMcpOverlay.None.IsSelectedByDefault("depot").Should().BeTrue();
    }

    [Fact]
    public void ApplyTo_AdditionalServer_IsAppended()
    {
        var overlay = new ProjectMcpOverlay
        {
            AdditionalServers = [new McpServerConfig { Name = "project-tools", Command = "uvx" }],
        };

        overlay.ApplyTo(Registry).Select(server => server.Name)
            .Should().Equal("youtrack", "depot", "project-tools");
    }

    [Fact]
    public void ApplyTo_AdditionalServerWithARegistryName_ReplacesItInPlace()
    {
        var overlay = new ProjectMcpOverlay
        {
            AdditionalServers = [new McpServerConfig { Name = "depot", Url = "https://project.example/mcp" }],
        };

        var effective = overlay.ApplyTo(Registry);

        effective.Should().HaveCount(2, "an override replaces the registry server rather than adding a second one of the same name");
        effective.Single(server => server.Name == "depot").Url.Should().Be("https://project.example/mcp");
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

        overlay.ApplyTo(Registry).Should().Contain(server => server.Name == "project-tools");
        overlay.IsSelectedByDefault("project-tools").Should().BeFalse();
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

        effective.Should().ContainSingle(server => server.Name == "project-tools")
            .Which.Command.Should().Be("first");
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

        overlay.ApplyTo(registry).Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }
}
