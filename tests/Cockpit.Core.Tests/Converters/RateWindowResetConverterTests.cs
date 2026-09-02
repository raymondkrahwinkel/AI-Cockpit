using System.Globalization;
using Cockpit.App.Converters;

namespace Cockpit.Core.Tests.Converters;

/// <summary>
/// The usage-pill flyout's reset line (AC-37): a relative "resets in 2h 14m" plus the absolute local time, from a
/// window's <c>ResetsAt</c>. A missing/invalid reset must yield an empty string (the row then shows just the bar),
/// and a reset already in the past must not render a negative duration.
/// </summary>
public class RateWindowResetConverterTests
{
    private static string Convert(object? value) =>
        (string)RateWindowResetConverter.Instance.Convert(value, typeof(string), null, CultureInfo.InvariantCulture)!;

    // No reset, or something that is not one at all: the row then shows just the bar rather than a stray word.
    [Theory]
    [InlineData(null)]
    [InlineData("not a date")]
    public void AValueThatIsNoReset_IsEmpty(object? value)
    {
        Assert.Empty(Convert(value));
    }

    [Fact]
    public void AFutureReset_ReadsRelativeThenAbsolute()
    {
        var resetsAt = DateTimeOffset.Now.AddHours(2).AddMinutes(14);

        var text = Convert(resetsAt);

        Assert.StartsWith("resets in ", text);
        Assert.Contains("2h", text);     // ~2h14m out — the hours are stable even if the minute ticks
        Assert.Contains(" · ", text);    // the absolute time follows the relative one
    }

    [Fact]
    public void ADayAwayReset_UsesDaysAndHours()
    {
        var text = Convert(DateTimeOffset.Now.AddDays(6).AddHours(14));

        Assert.Contains("6d", text);
        Assert.Contains("h", text);
    }

    [Fact]
    public void APastReset_SaysResetting_NotANegativeDuration()
    {
        var text = Convert(DateTimeOffset.Now.AddMinutes(-5));

        Assert.StartsWith("resetting", text);
        Assert.DoesNotContain("-", text);
    }
}
