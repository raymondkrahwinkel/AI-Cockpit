using System.Text.Json;
using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectDefinitionExtensionDataGuardTests
{
    [Fact]
    public void Apply_NullOrEmpty_ReturnsNullKeptAndNoDropped()
    {
        var (keptFromNull, droppedFromNull) = CockpitProjectDefinitionExtensionDataGuard.Apply(null);
        Assert.Null(keptFromNull);
        Assert.Empty(droppedFromNull);

        var (keptFromEmpty, droppedFromEmpty) = CockpitProjectDefinitionExtensionDataGuard.Apply([]);
        Assert.Null(keptFromEmpty);
        Assert.Empty(droppedFromEmpty);
    }

    [Fact]
    public void Apply_SecretShapedTopLevelPlaintextKey_IsDroppedAndReported()
    {
        var extensionData = new Dictionary<string, JsonElement>
        {
            ["newerSecretToken"] = JsonSerializer.SerializeToElement("plaintext-leak"),
        };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Null(kept);
        Assert.Equal(["newerSecretToken"], droppedKeys);
    }

    [Fact]
    public void Apply_SecretShapedTopLevelKey_AlreadyEncrypted_PassesThroughUntouched()
    {
        const string ciphertext = "enc:v1:AAAA";
        var extensionData = new Dictionary<string, JsonElement>
        {
            ["newerSecretToken"] = JsonSerializer.SerializeToElement(ciphertext),
        };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Empty(droppedKeys);
        Assert.Equal(ciphertext, kept!["newerSecretToken"].GetString());
    }

    [Fact]
    public void Apply_NonSecretShapedKey_PassesThroughUntouched()
    {
        var extensionData = new Dictionary<string, JsonElement>
        {
            ["someFutureField"] = JsonSerializer.SerializeToElement("forwarded as-is"),
        };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Empty(droppedKeys);
        Assert.Equal("forwarded as-is", kept!["someFutureField"].GetString());
    }

    [Fact]
    public void Apply_NestedObjectOneLevelDeep_SecretShapedChildIsDroppedAndReportedWithDottedPath()
    {
        var nested = JsonSerializer.SerializeToElement(new { apiToken = "plain", label = "kept" });
        var extensionData = new Dictionary<string, JsonElement> { ["integration"] = nested };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(extensionData);

        Assert.Equal(["integration.apiToken"], droppedKeys);
        var remaining = kept!["integration"];
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
        Assert.Equal(JsonValueKind.Array, kept!["tokens"].ValueKind);
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
        Assert.Equal("PLAINLEAK", kept!["integration"].GetProperty("auth").GetProperty("apiToken").GetString());
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
        Assert.Equal("PLAINLEAK", kept!["apiTokens"][0].GetString());
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
        Assert.Equal("PLAINLEAK", kept!["integrations"][0].GetProperty("apiToken").GetString());
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
        Assert.Contains("PLAINLEAK", kept!["config"].GetString());
    }

    // AC-607 review finding 4: a sensitive-field row's own [JsonExtensionData] passthrough is a second smuggling
    // seam the top-level Apply above never reaches on its own — ApplyToSensitiveFields closes that seam.
    [Fact]
    public void ApplyToSensitiveFields_RowHasSecretShapedPlaintextFallbackField_IsDroppedAndReported()
    {
        var row = new CockpitProjectSensitiveFieldEntry
        {
            Label = "Deploy token",
            Value = "enc:v1:AAAA",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["fallbackPassword"] = JsonSerializer.SerializeToElement("PLAINLEAK"),
            },
        };

        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.ApplyToSensitiveFields([row]);

        Assert.Equal(["SensitiveFields.Deploy token.fallbackPassword"], droppedKeys);
        // The row's ExtensionData had only this one key, so once dropped there is nothing left to keep (mirrors
        // Apply's own null-when-empty rule for the definition's top-level ExtensionData).
        Assert.Null(kept!.Single().ExtensionData);
    }

    [Fact]
    public void ApplyToSensitiveFields_NullOrEmpty_ReturnsNullKeptAndNoDropped()
    {
        var (keptFromNull, droppedFromNull) = CockpitProjectDefinitionExtensionDataGuard.ApplyToSensitiveFields(null);
        Assert.Null(keptFromNull);
        Assert.Empty(droppedFromNull);

        var (keptFromEmpty, droppedFromEmpty) = CockpitProjectDefinitionExtensionDataGuard.ApplyToSensitiveFields([]);
        Assert.Null(keptFromEmpty);
        Assert.Empty(droppedFromEmpty);
    }
}
