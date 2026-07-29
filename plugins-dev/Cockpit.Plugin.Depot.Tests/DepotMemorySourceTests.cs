
namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// <see cref="DepotMemorySource.Registration"/> — the fixed registration this plugin hands the host. The registry
/// that receives it (<c>ProjectMemorySourceRegistry.Register</c>) refuses a blank scheme, title or instruction, so
/// this is not cosmetic: a registration that regresses to blank here is one the host silently drops, and the
/// operator would never learn why Depot stopped appearing as a memory source.
/// </summary>
public class DepotMemorySourceTests
{
    [Fact]
    public void Registration_CarriesTheDepotScheme()
    {
        // The prefix a project's MemoryRef is matched against ("depot:cockpit") — already-linked projects are
        // keyed by it, so a change here silently unlinks them.
        Assert.Equal("depot", DepotMemorySource.Registration.Scheme);
    }

    [Fact]
    public void Registration_HasTheDepotTitle()
    {
        // Asserting the exact value (rather than merely NotBeNullOrWhiteSpace, which this equality already implies)
        // is what makes this test worth keeping: nothing else in this file pins Title's actual text, so a change to
        // it — including a regression to blank, which ProjectMemorySourceRegistry.Register would refuse — shows up
        // here.
        Assert.Equal("Depot project", DepotMemorySource.Registration.Title);
    }

    [Fact]
    public void Instruction_TellsTheSessionToUseTheDepotMcp()
    {
        Assert.Contains("Depot MCP", DepotMemorySource.Registration.Instruction);
    }

    [Fact]
    public void Instruction_CarriesTheHonestyClause_WhenTheMcpIsUnavailable()
    {
        // The behaviour that matters most: an agent that cannot see the Depot MCP in this session must say so
        // rather than quietly answering from memory it cannot actually verify.
        Assert.Contains("If the Depot MCP is not available in this session, say so rather than working from memory you cannot see.", DepotMemorySource.Registration.Instruction);
    }

    [Fact]
    public void Instruction_MatchesTheSpecExactly()
    {
        Assert.Equal("Read and write it through the Depot MCP: look the project up by that slug before you start, and "
                + "write back what you learn as you go. If the Depot MCP is not available in this session, say "
                + "so rather than working from memory you cannot see.", DepotMemorySource.Registration.Instruction);
    }
}
