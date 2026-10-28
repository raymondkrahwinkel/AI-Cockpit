using System.Diagnostics;
using Cockpit.App.Diagnostics;

namespace Cockpit.App.ViewTests;

public class OptionsOpenMeasurementTests
{
    [Fact]
    public void Finish_ReportsOnlyAtTheThreshold_WithTotalAndPhases()
    {
        long now = 0;
        var measurement = new OptionsOpenMeasurement(() => now);
        measurement.Mark("plugins");
        now = Stopwatch.Frequency * 849 / 1000;

        Assert.Null(measurement.Finish());

        long slowNow = 0;
        var slowMeasurement = new OptionsOpenMeasurement(() => slowNow);
        slowNow = Stopwatch.Frequency * 3 / 10;
        slowMeasurement.Mark("plugins");
        slowNow = Stopwatch.Frequency * 85 / 100;
        slowMeasurement.Mark("presented");

        Assert.Equal("options open slow total=850ms phases=plugins:300,presented:850", slowMeasurement.Finish());
    }
}
