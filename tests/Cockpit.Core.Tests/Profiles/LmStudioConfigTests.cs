using Cockpit.Core.Profiles;
using FluentAssertions;

namespace Cockpit.Core.Tests.Profiles;

/// <summary>
/// <see cref="LmStudioConfig"/>'s <c>ToString()</c> override (#45 review finding 4): a plain <c>record</c>'s
/// auto-generated <c>ToString()</c> would print <see cref="LmStudioConfig.ApiKey"/> in the clear — a leak
/// surface anywhere this config lands in a log line or exception message.
/// </summary>
public class LmStudioConfigTests
{
    [Fact]
    public void ToString_WithAnApiKey_RedactsIt()
    {
        var config = new LmStudioConfig("http://localhost:1234", "qwen2.5-7b-instruct", ApiKey: "super-secret-key");

        var text = config.ToString();

        text.Should().NotContain("super-secret-key");
        text.Should().Contain("***");
        text.Should().Contain("qwen2.5-7b-instruct");
        text.Should().Contain("http://localhost:1234");
    }

    [Fact]
    public void ToString_WithNoApiKey_ReportsNullRatherThanAnEmptyOrRedactedValue()
    {
        var config = new LmStudioConfig("http://localhost:1234", "qwen2.5-7b-instruct");

        var text = config.ToString();

        text.Should().Contain("ApiKey = null");
    }
}
