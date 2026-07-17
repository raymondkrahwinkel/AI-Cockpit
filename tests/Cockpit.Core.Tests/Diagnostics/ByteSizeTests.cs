using System.Globalization;
using Cockpit.Core.Diagnostics;
using FluentAssertions;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Formatting the two figures AC-57 turned on (AC-58): the resident 680 MB that reads clearly, and the 73.6 GB
/// virtual reservation that must keep its one decimal so it reads as a size rather than a wall of digits. The unit
/// is chosen to keep the number in range, and the decimal is dropped only once the fraction is noise (≥ 100).
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    public void Human_SmallValues_KeepTheirDetail(long bytes, string expected) =>
        ByteSize.Human(bytes).Should().Be(expected);

    [Fact]
    public void Human_TheResidentFigureThatMatters_ReadsAsMegabytes() =>
        ByteSize.Human(680L * 1024 * 1024).Should().Be("680 MB");

    // The reservation that started the "62 GB" panic: shown honestly as a large size, still one figure the eye can take in.
    [Fact]
    public void Human_TheVirtualReservation_KeepsOneDecimalInGigabytes() =>
        ByteSize.Human(79_000_000_000).Should().Be("73.6 GB");

    // Below 100 keeps a decimal; at or above it, the fraction is dropped as noise.
    [Fact]
    public void Human_CrossesToNoDecimal_AtOneHundred()
    {
        ByteSize.Human(99L * 1024 * 1024).Should().Be("99.0 MB");
        ByteSize.Human(100L * 1024 * 1024).Should().Be("100 MB");
    }

    // The formatter must not follow a comma-decimal locale, or the copied report would read "73,6 GB" on a Dutch machine.
    [Fact]
    public void Human_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            ByteSize.Human(79_000_000_000).Should().Be("73.6 GB");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
