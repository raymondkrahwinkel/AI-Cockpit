using System.Diagnostics;
using Cockpit.App.Diagnostics;

namespace Cockpit.App.ViewTests;

public class OptionsOpenMeasurementTests
{
    [Fact]
    public void Finish_ReportsOnlyAtTheThreshold_WithTotalAndPhases()
    {
        long now = 0;
        var measurement = new OptionsMeasurement("open", () => now);
        measurement.Mark("plugins");
        now = Stopwatch.Frequency * 849 / 1000;

        Assert.Null(measurement.Finish());

        long slowNow = 0;
        var slowMeasurement = new OptionsMeasurement("open", () => slowNow);
        slowNow = Stopwatch.Frequency * 3 / 10;
        slowMeasurement.Mark("plugins");
        slowNow = Stopwatch.Frequency * 85 / 100;
        slowMeasurement.Mark("presented");

        Assert.Equal("options open slow total=850ms phases=plugins:300,presented:850", slowMeasurement.Finish());

        var apply = new OptionsMeasurement("apply", () => now);
        var close = new OptionsMeasurement("close", () => now);
        Assert.Null(apply.Finish());
        Assert.Null(close.Finish());
        now += Stopwatch.Frequency * 85 / 100;
        apply.Mark("config");
        close.Mark("closed");
        Assert.Equal("options apply slow total=850ms phases=config:850", apply.Finish());
        Assert.Equal("options close slow total=850ms phases=closed:850", close.Finish());
    }
}
