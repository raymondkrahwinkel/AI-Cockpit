using Cockpit.Core.HeavyRuns;

namespace Cockpit.Core.Tests.HeavyRuns;

public class DiskSpaceForecastTests
{
    [Fact]
    public void RegressionAndPeakHourlyGrowthDeterminePressureWithoutStaleHistory()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var settings = new HeavyRunSettings();
        var forecast = new DiskSpaceForecast("drive");
        var first = forecast.Sample(now, 100_000, 80_000, settings);
        Assert.Null(first.TimeToFull);
        Assert.Equal(DiskPressure.Normal, first.Pressure);

        DiskSpaceSnapshot current = first;
        for (var minute = 1; minute <= 30; minute++)
        {
            current = forecast.Sample(now.AddMinutes(minute), 100_000, 80_000 - (minute * 100), settings);
        }

        Assert.Equal(TimeSpan.FromMinutes(770), current.TimeToFull);
        Assert.Equal(DiskPressure.Normal, current.Pressure);

        var pressure = new DiskSpaceForecast("pressure");
        pressure.Sample(now, 100_000, 35_000, settings);
        var elevated = pressure.Sample(now.AddMinutes(30), 100_000, 32_000, settings);
        Assert.Equal(TimeSpan.FromMinutes(320), elevated.TimeToFull);
        Assert.Equal(DiskPressure.Elevated, elevated.Pressure);
        Assert.Equal(DiskPressure.Normal, pressure.Sample(now.AddMinutes(31), 100_000, 32_000,
            settings with { DiskElevatedTimeToFullHours = 1 }).Pressure);

        var critical = new DiskSpaceForecast("critical");
        critical.Sample(now, 100_000, 9_000, settings);
        Assert.Equal(DiskPressure.Critical, critical.Sample(now.AddMinutes(30), 100_000, 5_000, settings).Pressure);

        var peak = new DiskSpaceForecast("peak");
        peak.Sample(now, 100_000, 40_000, settings);
        peak.Sample(now.AddHours(1), 100_000, 30_000, settings);
        peak.Sample(now.AddHours(2), 100_000, 30_000, settings);
        var stable = peak.Sample(now.AddHours(3), 100_000, 15_000, settings);
        Assert.Null(stable.TimeToFull);
        Assert.Equal(DiskPressure.Elevated, stable.Pressure);
        Assert.Equal(DiskPressure.Critical, peak.Sample(now.AddHours(4), 100_000, 9_000, settings).Pressure);
        Assert.Equal(DiskPressure.Normal, peak.Sample(now.AddHours(29), 100_000, 9_000, settings).Pressure);

        var retention = new DiskSpaceForecast("retention");
        retention.Sample(now, 100_000, 20_000, settings);
        retention.Sample(now.AddHours(1), 100_000, 9_000, settings);
        Assert.Equal(DiskPressure.Critical, retention.Sample(now.AddHours(25), 100_000, 9_000, settings).Pressure);
        Assert.Equal(DiskPressure.Normal, retention.Sample(now.AddHours(25).AddMinutes(1), 100_000, 9_000, settings).Pressure);

        var regression = new DiskSpaceForecast("regression");
        regression.Sample(now, 100_000, 90_000, settings);
        regression.Sample(now.AddMinutes(1), 100_000, 89_000, settings);
        regression.Sample(now.AddMinutes(2), 100_000, 90_000, settings);
        Assert.Equal(TimeSpan.FromMinutes(1_790), regression.Sample(now.AddMinutes(3), 100_000, 89_500, settings).TimeToFull);

        var free = new DiskSpaceForecast("free");
        Assert.Equal(DiskPressure.Normal, free.Sample(now, 100_000, 5_000, settings).Pressure);
        Assert.Equal(DiskPressure.Elevated, free.Sample(now.AddMinutes(1), 100_000, 4_999,
            settings with { DiskElevatedTimeToFullHours = 0, DiskCriticalTimeToFullHours = 0 }).Pressure);
        Assert.Equal(DiskPressure.Critical, new DiskSpaceForecast("low").Sample(now, 100_000, 2_499, settings).Pressure);
        Assert.Equal(DiskPressure.Elevated, new DiskSpaceForecast("half").Sample(now, 100_000, 2_500, settings).Pressure);
        Assert.Equal(DiskPressure.Elevated, new DiskSpaceForecast("custom").Sample(now, 100_000, 9_000,
            settings with { DiskMinimumFreePercent = 10 }).Pressure);
        var growingFree = new DiskSpaceForecast("growing-free");
        growingFree.Sample(now, 100_000, 80_000, settings);
        Assert.Null(growingFree.Sample(now.AddMinutes(1), 100_000, 81_000, settings).TimeToFull);
    }
}
