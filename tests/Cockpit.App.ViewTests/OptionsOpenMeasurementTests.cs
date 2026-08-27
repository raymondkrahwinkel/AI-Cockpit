using System.Diagnostics;
using Cockpit.App.Diagnostics;

namespace Cockpit.App.ViewTests;

public class OptionsOpenMeasurementTests
{
    [Fact]
    public void Finish_BelowTheThreshold_DoesNotProduceALogLine()
    {
        long now = 0;
        var measurement = new OptionsOpenMeasurement(() => now);
        measurement.Mark("plugins");
        now = Stopwatch.Frequency * 849 / 1000;

        Assert.Null(measurement.Finish());
    }

    [Fact]
    public void Finish_AtTheThreshold_ReportsTotalAndPhases()
    {
        long now = 0;
        var measurement = new OptionsOpenMeasurement(() => now);
        now = Stopwatch.Frequency * 3 / 10;
        measurement.Mark("plugins");
        now = Stopwatch.Frequency * 85 / 100;
        measurement.Mark("presented");

        Assert.Equal("options open slow total=850ms phases=plugins:300,presented:850", measurement.Finish());
    }
}
