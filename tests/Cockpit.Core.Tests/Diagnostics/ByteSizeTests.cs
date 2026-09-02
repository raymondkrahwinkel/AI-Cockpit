using System.Globalization;
using Cockpit.Core.Diagnostics;

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
        Assert.Equal(expected, ByteSize.Human(bytes));

    // Below 100 keeps a decimal; at or above it, the fraction is dropped as noise.
    [Fact]
    public void Human_CrossesToNoDecimal_AtOneHundred()
    {
        Assert.Equal("99.0 MB", ByteSize.Human(99L * 1024 * 1024));
        Assert.Equal("100 MB", ByteSize.Human(100L * 1024 * 1024));
    }

    // The formatter must not follow a comma-decimal locale, or the copied report would read "73,6 GB" on a Dutch machine.
    [Fact]
    public void Human_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            Assert.Equal("73.6 GB", ByteSize.Human(79_000_000_000));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
