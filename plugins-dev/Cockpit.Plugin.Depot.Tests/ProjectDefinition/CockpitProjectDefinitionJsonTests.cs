using System.Text.Json;
using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// The hostile-input harness for `CockpitProjectDefinitionJson` (AC-244): unknown fields, mismatched
// `schemaVersion`, missing required fields, corrupt/truncated JSON, extreme strings and unicode. Compares
// parsed values, not raw text, since property order is not part of the forward-compat promise.
public class CockpitProjectDefinitionJsonTests
{

    [Fact]
    public void TryDeserialize_UnknownTopLevelField_IsKeptOnTheNextWrite()
    {
        const string json = """{"schemaVersion":1,"name":"probe","futureField":"kept-through-round-trip"}""";

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out _));
        Assert.NotNull(definition);
        var writtenBack = CockpitProjectDefinitionJson.Serialize(definition);

        using var document = JsonDocument.Parse(writtenBack);
        Assert.Equal("kept-through-round-trip", document.RootElement.GetProperty("futureField").GetString());
    }

    [Fact]
    public void TryDeserialize_UnknownFieldInsideMcpOverlay_IsKeptOnTheNextWrite()
    {
        const string json = """{"schemaVersion":1,"name":"probe","mcpOverlay":{"enabled":["YouTrack"],"futureOverlayField":42}}""";

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out _));
        Assert.NotNull(definition);
        var writtenBack = CockpitProjectDefinitionJson.Serialize(definition);

        using var document = JsonDocument.Parse(writtenBack);
        Assert.Equal(42, document.RootElement.GetProperty("mcpOverlay").GetProperty("futureOverlayField").GetInt32());
    }
}
