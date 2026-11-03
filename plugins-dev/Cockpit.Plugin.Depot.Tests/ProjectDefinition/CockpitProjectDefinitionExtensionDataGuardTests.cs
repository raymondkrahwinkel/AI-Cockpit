using System.Text.Json;
using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// AC-607 limit, deliberately not pinned: the guard checks top-level keys and one level of nested objects only.
// A secret two levels deep, inside an array, in an array of objects or as JSON embedded in a string still travels.
public class CockpitProjectDefinitionExtensionDataGuardTests
{
    [Fact]
    public void Apply_NonSecretShapedKey_PassesThroughUntouched()
    {
        var extensionData = new Dictionary<string, JsonElement>
        {
            ["someFutureField"] = JsonSerializer.SerializeToElement("forwarded as-is"),
        };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Empty(droppedKeys);
        Assert.NotNull(kept);
        Assert.Equal("forwarded as-is", kept["someFutureField"].GetString());
    }

    [Fact]
    public void Apply_NestedObjectOneLevelDeep_SecretShapedChildIsDroppedAndReportedWithDottedPath()
    {
        var nested = JsonSerializer.SerializeToElement(new { apiToken = "plain", label = "kept" });
        var extensionData = new Dictionary<string, JsonElement> { ["integration"] = nested };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Equal(["integration.apiToken"], droppedKeys);
        Assert.NotNull(kept);
        var remaining = kept["integration"];
        Assert.False(remaining.TryGetProperty("apiToken", out _));
        Assert.Equal("kept", remaining.GetProperty("label").GetString());
    }
}
