using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Tests <see cref="CgroupPressureLine"/> against real `memory.pressure` content from this machine.
/// The `some` line is the one oomd decides on; `full` is a stricter figure a live session never reaches.
/// </summary>
public class CgroupPressureLineTests
{
    [Fact]
    public void ADecimalPoint_IsReadTheKernelsWayWhateverTheMachinesLocale()
    {
        // The kernel always writes '.', so a machine running a comma locale must not read 12.34 as 1234.
        using var dutch = new CultureSwitch("nl-NL");

        Assert.Equal(12.34, CgroupPressureLine.SomeAvg10("some avg10=12.34 avg60=1.00 avg300=0.10 total=5\n"));
    }

    // Restores the thread's culture whatever the assertion does, so one case cannot leak into the next.
    private sealed class CultureSwitch : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureSwitch(string name) =>
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
