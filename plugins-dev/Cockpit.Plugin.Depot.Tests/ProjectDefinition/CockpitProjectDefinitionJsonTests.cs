using System.Text.Json;
using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// The hostile-input harness for `CockpitProjectDefinitionJson` (AC-244): unknown fields, mismatched
// `schemaVersion`, missing required fields, corrupt/truncated JSON, extreme strings and unicode. Compares
// parsed values, not raw text, since property order is not part of the forward-compat promise.
public class CockpitProjectDefinitionJsonTests
{
    [Fact]
    public void Serialize_ThenDeserialize_AllKnownFieldsRoundTrip()
    {
        var definition = new CockpitProjectDefinition
        {
            Name = "Handbook",
            Description = "Handles handbook",
            GitUrl = "git@github.com:acme/handbook-processor.git",
            BehaviorPrompt = "Be careful with money.",
            IsolateInWorktreeByDefault = true,
            McpOverlay = new CockpitProjectMcpOverlayEntry { Enabled = ["Depot: Work", "YouTrack"] },
            Resources = [CockpitProjectResourceEntry.Create("Memory", "depot:handbook-processor")!],
            Logo = ".cockpit/logo.png",
        };

        var json = CockpitProjectDefinitionJson.Serialize(definition);
        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var roundTripped, out var error));

        Assert.Null(error);
        Assert.Equal(definition.Name, roundTripped!.Name);
        Assert.Equal(definition.Description, roundTripped.Description);
        Assert.Equal(definition.GitUrl, roundTripped.GitUrl);
        Assert.Equal(definition.BehaviorPrompt, roundTripped.BehaviorPrompt);
        Assert.Equal(definition.IsolateInWorktreeByDefault, roundTripped.IsolateInWorktreeByDefault);
        Assert.Equal(definition.McpOverlay.Enabled, roundTripped.McpOverlay!.Enabled);
        Assert.Equal(definition.Logo, roundTripped.Logo);
        Assert.Equal("depot:handbook-processor", Assert.Single(roundTripped.Resources!).Reference);
    }

    [Fact]
    public void TryDeserialize_UnknownTopLevelField_IsKeptOnTheNextWrite()
    {
        const string json = """{"schemaVersion":1,"name":"probe","futureField":"kept-through-round-trip"}""";

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out _));
        var writtenBack = CockpitProjectDefinitionJson.Serialize(definition!);

        using var document = JsonDocument.Parse(writtenBack);
        Assert.Equal("kept-through-round-trip", document.RootElement.GetProperty("futureField").GetString());
    }

    [Fact]
    public void TryDeserialize_UnknownFieldInsideMcpOverlay_IsKeptOnTheNextWrite()
    {
        const string json = """{"schemaVersion":1,"name":"probe","mcpOverlay":{"enabled":["YouTrack"],"futureOverlayField":42}}""";

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out _));
        var writtenBack = CockpitProjectDefinitionJson.Serialize(definition!);

        using var document = JsonDocument.Parse(writtenBack);
        Assert.Equal(42, document.RootElement.GetProperty("mcpOverlay").GetProperty("futureOverlayField").GetInt32());
    }

    [Fact]
    public void TryDeserialize_UnknownFieldInsideAResourceRow_IsKeptOnTheNextWrite()
    {
        const string json = """
            {"schemaVersion":1,"name":"probe","resources":[
                {"role":"Memory","reference":"depot:cockpit","futureRowField":"still here"}
            ]}
            """;

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out _));
        var writtenBack = CockpitProjectDefinitionJson.Serialize(definition!);

        using var document = JsonDocument.Parse(writtenBack);
        var row = document.RootElement.GetProperty("resources")[0];
        Assert.Equal("still here", row.GetProperty("futureRowField").GetString());
    }

    [Fact]
    public void TryDeserialize_ResourcesMixAllFourPortabilityShapes_EachRowSurvivesWithItsOwnValue()
    {
        const string json = """
            {"schemaVersion":1,"name":"probe","resources":[
                {"role":"Instructions","reference":"docs/CONVENTIONS.md","portability":"repo-relative"},
                {"role":"Reference","reference":"~/Notes/private.md","portability":"anchor-relative"},
                {"role":"Memory","reference":"depot:cockpit","portability":"plugin-source"},
                {"role":"Reference","reference":"/home/raymond/private.md","portability":"absolute"}
            ]}
            """;

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out var error));
        Assert.Null(error);
        Assert.Equal(4, definition!.Resources!.Count);
        Assert.Equal(
            ["repo-relative", "anchor-relative", "plugin-source", "absolute"],
            definition.Resources.Select(row => row.Portability));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"schemaVersion":1,"name":"trunc""")]
    [InlineData("{\"schemaVersion\":1,\"resources\":[{\"role\":\"Memory\",\"reference\":\"depot:cockpit\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData(null)]
    public void TryDeserialize_CorruptOrWrongShapedJson_ReturnsFalseRatherThanThrowing(string? corrupt)
    {
        var succeeded = CockpitProjectDefinitionJson.TryDeserialize(corrupt, out var definition, out var error);

        Assert.False(succeeded);
        Assert.Null(definition);
        Assert.NotNull(error);
    }
}
