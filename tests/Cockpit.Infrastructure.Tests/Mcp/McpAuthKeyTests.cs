using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// The key guarding the cockpit's loopback MCP endpoints (AC-40): only this run's key, presented as a bearer token,
/// is authorized; anything else — a wrong key, a bare token, a missing header — is turned away.
/// </summary>
public class McpAuthKeyTests
{
    [Fact]
    public void IsAuthorized_TheRunsOwnKeyAsABearerToken_IsAccepted()
    {
        var key = new McpAuthKey();

        Assert.True(key.IsAuthorized($"Bearer {key.Value}"));
    }

    [Fact]
    public void IsAuthorized_AWrongKey_IsRejected()
    {
        var key = new McpAuthKey();

        Assert.False(key.IsAuthorized("Bearer not-the-key"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsAuthorized_NoHeader_IsRejected(string? header) =>
        Assert.False(new McpAuthKey().IsAuthorized(header));

    [Fact]
    public void IsAuthorized_TheBareKeyWithoutTheBearerScheme_IsRejected()
    {
        var key = new McpAuthKey();

        Assert.False(key.IsAuthorized(key.Value));
    }

    [Fact]
    public void Value_IsFreshPerInstance_SoAKeyDoesNotSurviveARestart() =>
        Assert.NotEqual(new McpAuthKey().Value, new McpAuthKey().Value);

    [Fact]
    public void OneKeyDoesNotAuthorizeAnother()
    {
        var first = new McpAuthKey();
        var second = new McpAuthKey();

        Assert.False(first.IsAuthorized($"Bearer {second.Value}"));
    }
}
