using Cockpit.Plugin.Productivity.Contracts;

namespace Cockpit.Plugin.Productivity.Tests;

public sealed class DurationTextTests
{
    [Fact]
    public void Parse_AcceptsClockMinutesAndDutchOrInvariantHours()
    {
        foreach (var (text, expectedMinutes) in new[] { ("0:15", 15), ("1,5u", 90) })
        {
            Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), DurationText.Parse(text));
        }
    }
}
