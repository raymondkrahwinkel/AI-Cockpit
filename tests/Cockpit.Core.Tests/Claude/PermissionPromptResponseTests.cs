using System.Text.Json;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Locks the <c>--permission-prompt-tool</c> response contract verified against claude.exe
/// 2.1.197: allow carries <c>behavior</c>+<c>updatedInput</c>, deny carries
/// <c>behavior</c>+<c>message</c>.
/// </summary>
public class PermissionPromptResponseTests
{
    [Fact]
    public void Serialize_Allow_EchoesProposedInputAsUpdatedInput()
    {
        var proposed = """{"file_path":"a.txt","content":"hi"}""";

        var json = PermissionPromptResponse.Serialize(PermissionDecision.Allow(), proposed);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("allow", doc.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("a.txt", doc.RootElement.GetProperty("updatedInput").GetProperty("file_path").GetString());
        Assert.Equal("hi", doc.RootElement.GetProperty("updatedInput").GetProperty("content").GetString());
    }

    [Fact]
    public void Serialize_AllowWithRewrittenInput_UsesTheRewrittenInput()
    {
        var proposed = """{"file_path":"a.txt"}""";
        var rewritten = """{"file_path":"safe.txt"}""";

        var json = PermissionPromptResponse.Serialize(PermissionDecision.Allow(rewritten), proposed);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("safe.txt", doc.RootElement.GetProperty("updatedInput").GetProperty("file_path").GetString());
    }

    [Fact]
    public void Serialize_Deny_CarriesBehaviorAndMessage_AndNoUpdatedInput()
    {
        var json = PermissionPromptResponse.Serialize(PermissionDecision.Deny("nope"), proposedInputJson: "{}");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("deny", doc.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("nope", doc.RootElement.GetProperty("message").GetString());
        Assert.False(doc.RootElement.TryGetProperty("updatedInput", out _));
    }

    [Fact]
    public void Serialize_AllowWithNonJsonProposedInput_FallsBackToEmptyObject()
    {
        var json = PermissionPromptResponse.Serialize(PermissionDecision.Allow(), proposedInputJson: "not json");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("allow", doc.RootElement.GetProperty("behavior").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("updatedInput").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("updatedInput").EnumerateObject());
    }
}
