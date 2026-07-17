using Cockpit.Infrastructure.Mcp;
using FluentAssertions;

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

        key.IsAuthorized($"Bearer {key.Value}").Should().BeTrue();
    }

    [Fact]
    public void IsAuthorized_AWrongKey_IsRejected()
    {
        var key = new McpAuthKey();

        key.IsAuthorized("Bearer not-the-key").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsAuthorized_NoHeader_IsRejected(string? header) =>
        new McpAuthKey().IsAuthorized(header).Should().BeFalse();

    [Fact]
    public void IsAuthorized_TheBareKeyWithoutTheBearerScheme_IsRejected()
    {
        var key = new McpAuthKey();

        key.IsAuthorized(key.Value).Should().BeFalse();
    }

    [Fact]
    public void Value_IsFreshPerInstance_SoAKeyDoesNotSurviveARestart() =>
        new McpAuthKey().Value.Should().NotBe(new McpAuthKey().Value);

    [Fact]
    public void OneKeyDoesNotAuthorizeAnother()
    {
        var first = new McpAuthKey();
        var second = new McpAuthKey();

        first.IsAuthorized($"Bearer {second.Value}").Should().BeFalse();
    }
}
