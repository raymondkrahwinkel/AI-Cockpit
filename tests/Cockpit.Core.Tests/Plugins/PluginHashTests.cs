using System.Text;
using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The SHA-256 pin used to detect a changed/tampered plugin assembly (#14).</summary>
public class PluginHashTests
{
    [Fact]
    public void Compute_IsDeterministic_AndLowercaseHex()
    {
        var bytes = Encoding.UTF8.GetBytes("plugin-assembly-contents");

        var first = PluginHash.Compute(bytes);
        var second = PluginHash.Compute(bytes);

        first.Should().Be(second);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Compute_DifferentBytes_DifferentHash()
    {
        PluginHash.Compute(Encoding.UTF8.GetBytes("v1"))
            .Should().NotBe(PluginHash.Compute(Encoding.UTF8.GetBytes("v2")));
    }
}
