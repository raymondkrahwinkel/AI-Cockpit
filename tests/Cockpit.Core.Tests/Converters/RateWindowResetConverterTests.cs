using System.Globalization;
using Cockpit.App.Converters;
using FluentAssertions;

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

    [Fact]
    public void Null_IsEmpty()
    {
        Convert(null).Should().BeEmpty();
    }

    [Fact]
    public void ANonDateValue_IsEmpty()
    {
        Convert("not a date").Should().BeEmpty();
    }

    [Fact]
    public void AFutureReset_ReadsRelativeThenAbsolute()
    {
        var resetsAt = DateTimeOffset.Now.AddHours(2).AddMinutes(14);

        var text = Convert(resetsAt);

        text.Should().StartWith("resets in ");
        text.Should().Contain("2h");     // ~2h14m out — the hours are stable even if the minute ticks
        text.Should().Contain(" · ");    // the absolute time follows the relative one
    }

    [Fact]
    public void ADayAwayReset_UsesDaysAndHours()
    {
        var text = Convert(DateTimeOffset.Now.AddDays(6).AddHours(14));

        text.Should().Contain("6d");
        text.Should().Contain("h");
    }

    [Fact]
    public void APastReset_SaysResetting_NotANegativeDuration()
    {
        var text = Convert(DateTimeOffset.Now.AddMinutes(-5));

        text.Should().StartWith("resetting");
        text.Should().NotContain("-");
    }
}
