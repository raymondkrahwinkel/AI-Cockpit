using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// <see cref="DepotMemorySource.BuildRegistrationPairs"/> (AC-501) — one registration per connection instead of the
/// single fixed one this plugin handed the host before. The registry that receives it
/// (<c>ProjectMemorySourceRegistry.Register</c>) refuses a blank scheme, title or instruction, so this is not
/// cosmetic: a registration that regresses to blank here is one the host silently drops, and the operator would
/// never learn why a connection stopped appearing as a memory source.
/// </summary>
public class DepotMemorySourceTests
{
    private static DepotConnectionRegistration Connection(string id, string name, string url = "https://depot.example.com") =>
        new(id, name, url);

    [Fact]
    public void FirstConnection_KeepsThePlainDepotScheme()
    {
        // The existing-projects compatibility guarantee (AC-501 acceptance criterion 3): a project stored as
        // "depot:cockpit" before this ticket must keep resolving, whichever connection is now first.
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Synvolution")]);

        Assert.Equal("depot", Assert.Single(pairs).Registration.Scheme);
    }

    [Fact]
    public void SecondConnection_GetsASchemeNamespacedFromItsOwnName()
    {
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("c1", "Synvolution"),
            Connection("c2", "Wispslate"),
        ]);

        Assert.Equal("depot", pairs[0].Registration.Scheme);
        Assert.Equal("depot.wispslate", pairs[1].Registration.Scheme);
    }

    [Fact]
    public void EachRegistration_HasATitleDerivedFromTheConnectionName()
    {
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("c1", "Synvolution"),
            Connection("c2", "Wispslate"),
        ]);

        Assert.Equal("Depot project — Synvolution", pairs[0].Registration.Title);
        Assert.Equal("Depot project — Wispslate", pairs[1].Registration.Title);
    }

    [Fact]
    public void EachRegistration_InstructionNamesItsOwnInstance()
    {
        // Acceptance criterion 7: the Instruction has to say *which* Depot a starting session's memory lives on,
        // not just that it lives on Depot.
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Wispslate")]);

        Assert.Contains("Wispslate", pairs.Single().Registration.Instruction);
    }

    [Fact]
    public void Instruction_StillCarriesTheHonestyClause_WhenTheMcpIsUnavailable()
    {
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Synvolution")]);

        Assert.Contains(
            "If the Depot MCP is not available in this session, say so rather than working from memory you cannot see.",
            pairs.Single().Registration.Instruction);
    }

    [Fact]
    public void NoConnections_BuildsNoRegistrations()
    {
        // Acceptance criterion 5: without a configured connection the row behaves exactly as it did before this
        // plugin existed — nothing offered, not a fixed "Depot project" nothing points at.
        Assert.Empty(DepotMemorySource.BuildRegistrationPairs([]));
    }

    [Fact]
    public void ASymbolOnlyName_StillProducesAUsableScheme()
    {
        // Nothing in the slug survives a name like "★★★" — the connection's own id is the fallback, which is always
        // ProjectMemoryRef.IsUsableScheme-valid (a GUID's hex digits), so this connection is never silently dropped.
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("c1", "Primary"),
            Connection("abc123", "★★★"),
        ]);

        // Mirrors ProjectMemoryRef.IsUsableScheme (Cockpit.Core, not referenced from this test project): at least
        // two characters, no colon, no surrounding whitespace.
        var scheme = pairs[1].Registration.Scheme;
        Assert.Equal("depot.abc123", scheme);
        Assert.True(scheme.Length >= 2 && !scheme.Contains(':') && scheme == scheme.Trim());
    }

    [Fact]
    public void ThreeNonPrimaryConnectionsSharingBothNameAndId_StillGetDistinctSchemes()
    {
        // Pathological input (hand-edited or corrupted storage sharing an id) — three, not two: the second
        // collision already falls back to the shared id, so only a third one proves the id-fallback itself is
        // re-checked against what is already taken rather than assumed to always be free.
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("primary", "Synvolution"),
            Connection("dup", "Work"),
            Connection("dup", "Work"),
            Connection("dup", "Work"),
        ]);

        var schemes = pairs.Skip(1).Select(pair => pair.Registration.Scheme).ToList();
        Assert.Equal(schemes.Count, schemes.Distinct().Count());
    }

    [Fact]
    public void TwoNonPrimaryConnectionsWithTheSameName_StillGetDistinctSchemes()
    {
        // The slug alone would collide on "depot.work" for both non-primary connections; the second one to claim it
        // falls back to its own id instead of silently losing to the registry's first-one-wins Register refusal.
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("primary", "Synvolution"),
            Connection("c1", "Work"),
            Connection("c2", "Work"),
        ]);

        Assert.Equal("depot.work", pairs[1].Registration.Scheme);
        Assert.Equal("depot.c2", pairs[2].Registration.Scheme);
    }

    [Fact]
    public void BuildRegistrations_ReturnsTheSameRegistrationsAsThePairs()
    {
        var connections = new[] { Connection("c1", "Synvolution"), Connection("c2", "Wispslate") };

        var registrations = DepotMemorySource.BuildRegistrations(connections);
        var pairs = DepotMemorySource.BuildRegistrationPairs(connections);

        Assert.Equal(pairs.Select(pair => pair.Registration), registrations);
    }
}
