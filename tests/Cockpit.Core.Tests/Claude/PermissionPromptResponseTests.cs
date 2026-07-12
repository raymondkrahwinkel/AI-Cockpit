using System.Text.Json;
using FluentAssertions;
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
        doc.RootElement.GetProperty("behavior").GetString().Should().Be("allow");
        doc.RootElement.GetProperty("updatedInput").GetProperty("file_path").GetString().Should().Be("a.txt");
        doc.RootElement.GetProperty("updatedInput").GetProperty("content").GetString().Should().Be("hi");
    }

    [Fact]
    public void Serialize_AllowWithRewrittenInput_UsesTheRewrittenInput()
    {
        var proposed = """{"file_path":"a.txt"}""";
        var rewritten = """{"file_path":"safe.txt"}""";

        var json = PermissionPromptResponse.Serialize(PermissionDecision.Allow(rewritten), proposed);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("updatedInput").GetProperty("file_path").GetString().Should().Be("safe.txt");
    }

    [Fact]
    public void Serialize_Deny_CarriesBehaviorAndMessage_AndNoUpdatedInput()
    {
        var json = PermissionPromptResponse.Serialize(PermissionDecision.Deny("nope"), proposedInputJson: "{}");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("behavior").GetString().Should().Be("deny");
        doc.RootElement.GetProperty("message").GetString().Should().Be("nope");
        doc.RootElement.TryGetProperty("updatedInput", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_AllowWithNonJsonProposedInput_FallsBackToEmptyObject()
    {
        var json = PermissionPromptResponse.Serialize(PermissionDecision.Allow(), proposedInputJson: "not json");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("behavior").GetString().Should().Be("allow");
        doc.RootElement.GetProperty("updatedInput").ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("updatedInput").EnumerateObject().Should().BeEmpty();
    }
}
