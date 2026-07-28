using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Which memory sources the project editor's picker and a starting session's standing instructions both end up
/// reading (AC-165/166). Case-insensitive on purpose, unlike <see cref="ProjectFieldRegistryTests"/>'s key
/// comparison: a project's own <c>MemoryRef</c> is matched the same way when it is read back
/// (<see cref="Cockpit.Core.Sessions.SessionStartDefaults"/>), so the registry has to agree with that rule rather
/// than the field registry's.
/// </summary>
public class ProjectMemorySourceRegistryTests
{
    private static ProjectMemorySourceRegistration Source(string scheme, string title, string instruction = "Read it there.") =>
        new(scheme, title, instruction);

    [Fact]
    public void Register_TwoPluginsOfferingTheSameScheme_KeepsTheFirst()
    {
        var registry = new ProjectMemorySourceRegistry();

        registry.Register(Source("depot", "Depot project")).Should().BeTrue();
        registry.Register(Source("depot", "Depot (second copy)")).Should().BeFalse();

        registry.Sources.Should().ContainSingle().Which.Title.Should().Be("Depot project");
    }

    [Fact]
    public void Register_SchemesDifferingOnlyInCase_AreTheSameSource()
    {
        // A project's MemoryRef is matched case-insensitively when it is read back (SessionStartDefaults), so two
        // schemes differing only in case would otherwise let a second plugin silently shadow the first's meaning.
        var registry = new ProjectMemorySourceRegistry();

        registry.Register(Source("depot", "Depot project")).Should().BeTrue();
        registry.Register(Source("Depot", "Depot (again)")).Should().BeFalse();

        registry.Sources.Should().ContainSingle();
    }

    [Fact]
    public void Register_ABlankScheme_IsRefused()
    {
        var registry = new ProjectMemorySourceRegistry();

        registry.Register(Source("   ", "Nameless")).Should().BeFalse();

        registry.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Register_AOneCharacterScheme_IsRefused()
    {
        // Below the floor TryParse itself enforces on the text before the colon: a scheme this short is one the
        // parser could never split out of a stored reference, so registering it would offer a choice that then
        // falls silent for every project that picks it.
        var registry = new ProjectMemorySourceRegistry();

        registry.Register(Source("d", "One-letter source")).Should().BeFalse();

        registry.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Register_ASchemeContainingAColon_IsRefused()
    {
        // TryParse splits a reference on its first colon, so the scheme it hands back never contains one. A scheme
        // that does could never be the text TryParse extracts, whatever reference stored it.
        var registry = new ProjectMemorySourceRegistry();

        registry.Register(Source("de:pot", "Colon in the scheme")).Should().BeFalse();

        registry.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Register_ASchemeWithSurroundingWhitespace_IsRefused()
    {
        // A starting session trims the stored reference before parsing it (SessionStartDefaults), while the project
        // editor parses what it saved as-is. A scheme with surrounding whitespace could therefore match in one of
        // those two places and not the other for the same stored reference — offered in the dialog, silent in the
        // session, or the reverse.
        var registry = new ProjectMemorySourceRegistry();

        registry.Register(Source(" depot", "Leading space")).Should().BeFalse();

        registry.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Register_ABlankTitle_IsRefused()
    {
        var registry = new ProjectMemorySourceRegistry();

        registry.Register(Source("depot", "  ")).Should().BeFalse();

        registry.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Register_ABlankInstruction_IsRefused()
    {
        // Not the same kind of gap as a blank title: a source that cannot tell the session how to reach it leaves
        // that session no better off than the bare reference it would have been handed anyway, which is the one
        // thing this seam exists to fix. Offer it not at all rather than half-working.
        var registry = new ProjectMemorySourceRegistry();

        registry.Register(Source("depot", "Depot project", "   ")).Should().BeFalse();

        registry.Sources.Should().BeEmpty();
    }

    [Fact]
    public void TheAppsOwnScan_ResolvesTheRegistry()
    {
        var services = new ServiceCollection();
        services.AddServices(typeof(ProjectMemorySourceRegistry).Assembly);

        services.BuildServiceProvider().GetService<IProjectMemorySourceRegistry>().Should().BeOfType<ProjectMemorySourceRegistry>();
    }

    [Fact]
    public void Sources_AreOfferedInRegistrationOrder()
    {
        // Registered "notes" then "depot" — the reverse of alphabetical order — so a Sources implementation that
        // quietly sorted by scheme would fail this the same as one that got registration order wrong; registering
        // depot-then-notes could not tell the two apart.
        var registry = new ProjectMemorySourceRegistry();
        registry.Register(Source("notes", "Notes vault"));
        registry.Register(Source("depot", "Depot project"));

        registry.Sources.Select(source => source.Scheme).Should().Equal("notes", "depot");
    }
}
