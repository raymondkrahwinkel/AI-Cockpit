using System.Text.Json;
using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// The forward-compat promise of `CockpitProjectDefinitionJson` (AC-244, AC-246): a field this build does not know,
// at the top level, inside the MCP overlay or on a resource row a newer build wrote, parses cleanly and is kept on
// the next write instead of lost. Compares parsed values, not raw text, since property order is not part of it.
public class CockpitProjectDefinitionJsonTests
{
    [Fact]
    public void TryDeserialize_UnknownFieldsAnywhere_AreKeptOnTheNextWrite()
    {
        const string json = """
            {"schemaVersion":1,"name":"probe","futureField":"kept-through-round-trip",
             "mcpOverlay":{"enabled":["YouTrack"],"futureOverlayField":42},
             "resources":[{"role":"Reference","label":"Productie-DB","someFutureResourceField":"forwarded as-is"}]}
            """;

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out var error), error);
        Assert.NotNull(definition);
        var resource = Assert.Single(definition.Resources!);
        Assert.Equal(string.Empty, resource.Reference);
        Assert.False(resource.Placeholder); // unrecognised to THIS build's own "placeholder" key — false is correct, not a guess

        using var document = JsonDocument.Parse(CockpitProjectDefinitionJson.Serialize(definition));
        var root = document.RootElement;
        Assert.Equal("kept-through-round-trip", root.GetProperty("futureField").GetString());
        Assert.Equal(42, root.GetProperty("mcpOverlay").GetProperty("futureOverlayField").GetInt32());
        Assert.Equal("forwarded as-is", root.GetProperty("resources")[0].GetProperty("someFutureResourceField").GetString());
    }
}
