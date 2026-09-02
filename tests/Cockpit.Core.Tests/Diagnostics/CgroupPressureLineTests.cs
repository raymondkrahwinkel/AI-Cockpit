using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Tests <see cref="CgroupPressureLine"/> against real `memory.pressure` content from this machine.
/// The `some` line is the one oomd decides on; `full` is a stricter figure a live session never reaches.
/// </summary>
public class CgroupPressureLineTests
{
    private const string Quiet =
        "some avg10=0.00 avg60=0.00 avg300=0.00 total=1163\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=1162\n";

    private const string Stalling =
        "some avg10=90.80 avg60=71.12 avg300=30.44 total=262148711\nfull avg10=64.20 avg60=52.01 avg300=22.13 total=201773115\n";

    // 90.80 is the figure oomd printed for the slice it killed on 2026-08-25 — the `some` line. The `full` line's
    // 64.20 sits right beside it in the same file and would be the wrong meter.
    [Theory]
    [InlineData(Quiet, 0.0)]
    [InlineData(Stalling, 90.80)]
    public void ItReadsTheSomeLine_NotTheFullOne(string file, double expected) =>
        Assert.Equal(expected, CgroupPressureLine.SomeAvg10(file));

    [Fact]
    public void ADecimalPoint_IsReadTheKernelsWayWhateverTheMachinesLocale()
    {
        // The kernel always writes '.', so a machine running a comma locale must not read 12.34 as 1234.
        using var dutch = new CultureSwitch("nl-NL");

        Assert.Equal(12.34, CgroupPressureLine.SomeAvg10("some avg10=12.34 avg60=1.00 avg300=0.10 total=5\n"));
    }

    [Fact]
    public void AnythingThatIsNotAPressureFile_ReadsAsNothing()
    {
        Assert.Null(CgroupPressureLine.SomeAvg10(string.Empty));
        Assert.Null(CgroupPressureLine.SomeAvg10("full avg10=1.00 avg60=0.00 avg300=0.00 total=1\n"));
        Assert.Null(CgroupPressureLine.SomeAvg10("some avg60=1.00 total=1\n"));
        Assert.Null(CgroupPressureLine.SomeAvg10("some avg10=nonsense avg60=0.00\n"));
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
