using System.Text.Json;
using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

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

    [Fact]
    public void Apply_ArrayValue_IsNotWalkedAndPassesThroughUntouched()
    {
        var array = JsonSerializer.SerializeToElement(new[] { "token-in-an-array" });
        var extensionData = new Dictionary<string, JsonElement> { ["tokens"] = array };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Empty(droppedKeys);
        Assert.NotNull(kept);
        Assert.Equal(JsonValueKind.Array, kept["tokens"].ValueKind);
    }

    // AC-607 review finding 3: the guard's scope is top-level plus one level of nested objects, no arrays, no
    // JSON-embedded-in-a-string — a documented, defensible limit (ProjectResourceSecretPathHeuristic-style), not an
    // oversight. These 4 pin that the limit is real, so a future accidental change to it is visible, not silent.
    [Fact]
    public void Apply_SecretShapedKeyTwoLevelsDeep_StillForwardsUnencrypted()
    {
        var nested = JsonSerializer.SerializeToElement(new { auth = new { apiToken = "PLAINLEAK" } });
        var extensionData = new Dictionary<string, JsonElement> { ["integration"] = nested };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Empty(droppedKeys);
        Assert.NotNull(kept);
        Assert.Equal("PLAINLEAK", kept["integration"].GetProperty("auth").GetProperty("apiToken").GetString());
    }

    [Fact]
    public void Apply_SecretShapedKeyHoldingAnArray_StillForwardsUnencrypted()
    {
        var extensionData = new Dictionary<string, JsonElement>
        {
            ["apiTokens"] = JsonSerializer.SerializeToElement(new[] { "PLAINLEAK" }),
        };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Empty(droppedKeys);
        Assert.NotNull(kept);
        Assert.Equal("PLAINLEAK", kept["apiTokens"][0].GetString());
    }

    [Fact]
    public void Apply_ArrayOfObjectsWithASecretShapedKey_StillForwardsUnencrypted()
    {
        var extensionData = new Dictionary<string, JsonElement>
        {
            ["integrations"] = JsonSerializer.SerializeToElement(new[] { new { apiToken = "PLAINLEAK" } }),
        };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Empty(droppedKeys);
        Assert.NotNull(kept);
        Assert.Equal("PLAINLEAK", kept["integrations"][0].GetProperty("apiToken").GetString());
    }

    [Fact]
    public void Apply_SecretShapedValueEmbeddedAsJsonInAString_StillForwardsUnencrypted()
    {
        var extensionData = new Dictionary<string, JsonElement>
        {
            ["config"] = JsonSerializer.SerializeToElement("""{"password":"PLAINLEAK"}"""),
        };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Empty(droppedKeys);
        Assert.NotNull(kept);
        Assert.Contains("PLAINLEAK", kept["config"].GetString());
    }
}
