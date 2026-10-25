using Cockpit.Plugin.Productivity.Contracts;

namespace Cockpit.Plugin.Productivity.Tests;

public sealed class DurationTextTests
{
    [Theory]
    [InlineData("0:15", 15)]
    [InlineData("1,5u", 90)]
    public void Parse_AcceptsClockMinutesAndDutchOrInvariantHours(string text, int? expectedMinutes)
    {
        var parsed = DurationText.Parse(text);

        if (expectedMinutes is null)
        {
            Assert.Null(parsed);
        }
        else
        {
            Assert.Equal(TimeSpan.FromMinutes(expectedMinutes.Value), parsed);
        }
    }
}
