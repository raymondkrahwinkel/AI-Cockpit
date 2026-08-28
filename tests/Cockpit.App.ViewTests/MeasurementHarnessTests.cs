using Cockpit.MeasurementHarness.Core;
using Cockpit.MeasurementHarness.Meters;
using Cockpit.MeasurementHarness;

namespace Cockpit.App.ViewTests;

// AC-1131: the decision functions of the measurement harness, which are the part that has to be right for
// any of its numbers to mean anything. They need no window and no compositor, so they run in CI — the rest
// of the harness does not, and says so (see the harness README, "the CI boundary").
public sealed class MeasurementHarnessTests
{
    private static RunIdentity _Identity(string[]? args = null, string sha = "7d331771") =>
        RunIdentity.Capture("test", args ?? ["--a=1"], new Dictionary<string, string> { ["a"] = "1" }, () => sha);

    [Fact]
    public void Render_clock_in_headless_mode_is_refused_before_the_scenario_starts()
    {
        var options = new Options(Options.Parse(["--scenario=render-clock", "--headless=true"]));

        Assert.Contains("no compositor", options.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Median_shape_allows_a_noisy_plateau()
    {
        var medians = Series.MedianByX([
            new SeriesPoint(2, 4), new SeriesPoint(2, 5), new SeriesPoint(2, 5),
            new SeriesPoint(3, 3), new SeriesPoint(3, 6), new SeriesPoint(3, 8),
            new SeriesPoint(4, 6), new SeriesPoint(4, 8), new SeriesPoint(4, 10),
            new SeriesPoint(5, 6), new SeriesPoint(5, 8), new SeriesPoint(5, 8),
            new SeriesPoint(6, 8), new SeriesPoint(6, 8), new SeriesPoint(6, 9),
        ]);

        Assert.True(Series.Monotonic("sessions -> median worst frame rounds", medians).Holds);
    }

    [Fact]
    public void Median_shape_rejects_a_genuine_drop()
    {
        var medians = Series.MedianByX([
            new SeriesPoint(2, 6), new SeriesPoint(2, 7), new SeriesPoint(2, 8),
            new SeriesPoint(3, 8), new SeriesPoint(3, 9), new SeriesPoint(3, 10),
            new SeriesPoint(4, 4), new SeriesPoint(4, 5), new SeriesPoint(4, 6),
        ]);

        Assert.False(Series.Monotonic("sessions -> median worst frame rounds", medians).Holds);
    }

    [Fact]
    public void Median_shape_allows_one_pass_to_collapse_at_the_highest_session_count()
    {
        var medians = Series.MedianByX([
            new SeriesPoint(2, 6), new SeriesPoint(2, 6), new SeriesPoint(2, 6),
            new SeriesPoint(3, 9), new SeriesPoint(3, 9), new SeriesPoint(3, 9),
            new SeriesPoint(4, 11), new SeriesPoint(4, 11), new SeriesPoint(4, 11),
            new SeriesPoint(5, 159), new SeriesPoint(5, 159), new SeriesPoint(5, 159),
            new SeriesPoint(6, 23), new SeriesPoint(6, 159), new SeriesPoint(6, 159),
        ]);

        Assert.True(Series.Monotonic("sessions -> median worst frame rounds", medians).Holds);
    }

    // E1. The fault this replaces: ac1104's positive control silently stopped firing, and every report it
    // produced said "Infinite layout loop detected : 0" — which is what a healthy app says too (AC-1171).
    [Fact]
    public void A_run_that_never_exercised_its_control_has_no_report()
    {
        var run = new MeasurementRun(_Identity(), PositiveControl.Named("x", _ => Task.FromResult(true)));

        var thrown = Assert.Throws<InvalidOperationException>(() => run.Finish());

        Assert.Contains("RunControlAsync() was never called", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_control_that_does_not_fire_makes_the_run_a_malfunction()
    {
        var run = new MeasurementRun(_Identity(), PositiveControl.Named("blind", _ => Task.FromResult(false)));
        await run.RunControlAsync();

        var outcome = run.Finish();

        Assert.False(outcome.Trustworthy);
        Assert.Equal("MALFUNCTION", outcome.Verdict);
        Assert.Contains("did not fire", outcome.Report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declaring_that_there_is_no_control_is_allowed_but_lands_in_the_header()
    {
        var run = new MeasurementRun(_Identity(), PositiveControl.None("nothing here can be made to go off"));
        await run.RunControlAsync();

        var outcome = run.Finish();

        Assert.True(outcome.Trustworthy);
        Assert.Contains("none declared", outcome.Report, StringComparison.Ordinal);
    }

    // E2. The fault this replaces: a phase marker reading "expecting `uifreeze hang`" went through the same
    // list a text count ran over, so the harness reported one detection with zero detector lines (AC-1174).
    [Fact]
    public void A_phase_marker_cannot_be_counted_as_the_detector_it_names()
    {
        var recorder = new Recorder();
        recorder.Mark("phase 2 — holding the UI thread for 8s, expecting uifreeze hang");

        Assert.Equal(0, recorder.DetectorCount("uifreeze hang"));

        recorder.Detected("uifreeze hang", "uifreeze hang for=8.0s");

        Assert.Equal(1, recorder.DetectorCount("uifreeze hang"));
    }

    // E3. The fault this replaces: a forced full GC ahead of the measurement window took 17,1 s on a 10,2 GB
    // heap and set off the app's own freeze detector, which was nearly written up as a finding (AC-1184).
    [Fact]
    public async Task Measuring_after_verification_has_begun_is_refused()
    {
        var run = new MeasurementRun(_Identity(), PositiveControl.None("not needed for this test"));
        run.Verify("expensive check", _ => { });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => run.MeasureAsync("too late", _ => Task.CompletedTask));

        Assert.Contains("Verification is last by construction", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_blocking_collection_cannot_be_taken_inside_the_measurement_window()
    {
        var run = new MeasurementRun(_Identity(), PositiveControl.None("not needed for this test"));
        var gc = new GcMeter();
        gc.Start();

        InvalidOperationException? thrown = null;
        await run.MeasureAsync("window", _ =>
        {
            thrown = Assert.Throws<InvalidOperationException>(() => gc.ReachableBytes(run));
            return Task.CompletedTask;
        });

        Assert.NotNull(thrown);
        Assert.Contains("verification step, not a measurement", thrown.Message, StringComparison.Ordinal);
    }

    // E4. The fault this replaces: two runs differing only in --scrollintoview wrote the same file with the
    // same header, and which file held which measurement can no longer be established (AC-1177).
    [Fact]
    public void A_report_cannot_claim_a_checkout_it_cannot_name()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => RunIdentity.Capture("test", ["--a=1"], new Dictionary<string, string>(), () => null));

        Assert.Contains("COCKPIT_GIT_SHA is required", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_runs_that_differ_in_one_flag_do_not_share_a_report_name()
    {
        var withFlag = _Identity(["--mode=rail", "--scrollintoview"]);
        var withoutFlag = _Identity(["--mode=rail"]);

        Assert.NotEqual(withFlag.ReportFileName, withoutFlag.ReportFileName);
    }

    [Fact]
    public void A_second_run_with_the_same_argv_refuses_rather_than_overwrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ac1131-{Guid.NewGuid():N}");
        try
        {
            var identity = _Identity(["--mode=rail"]);
            var path = identity.WriteReport(directory, "the original evidence");

            var thrown = Assert.Throws<ReportCollisionException>(() => identity.WriteReport(directory, "a later run"));

            Assert.Equal(path, thrown.Path);
            Assert.Equal("the original evidence", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void The_header_lists_the_flags_that_are_off_as_well()
    {
        var identity = RunIdentity.Capture(
            "test",
            ["--mode=rail"],
            new Dictionary<string, string> { ["mode"] = "rail", ["scrollintoview"] = "false" },
            () => "7d331771");

        var header = string.Join("\n", identity.HeaderLines());

        Assert.Contains("scrollintoview = false", header, StringComparison.Ordinal);
        Assert.Contains("cockpit-sha=7d331771", header, StringComparison.Ordinal);
    }

    // E5. The fault this replaces: 7452 rounds at 15 tiles against 3146 at 20 — every run individually sound,
    // so no instrument check could see it. The defect only existed between the runs (AC-1176).
    [Fact]
    public void The_archive_series_with_its_outlier_does_not_hold_and_the_outlier_is_named()
    {
        var points = new List<SeriesPoint>
        {
            new(1, 210), new(5, 827), new(10, 1598), new(15, 7452), new(20, 3146), new(25, 3913), new(30, 4684),
        };

        var verdict = Series.Linear("tiles -> rounds", points);

        Assert.False(verdict.Holds);
        Assert.Contains(verdict.Outliers, p => p.X == 15);
        Assert.Contains("DOES NOT HOLD", verdict.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_series_without_the_outlier_holds()
    {
        var points = new List<SeriesPoint>
        {
            new(1, 210), new(5, 827), new(10, 1598), new(15, 2371), new(20, 3146), new(25, 3913), new(30, 4684),
        };

        var verdict = Series.Linear("tiles -> rounds", points);

        Assert.True(verdict.Holds);
        Assert.Empty(verdict.Outliers);
    }

    [Fact]
    public void A_series_too_short_to_judge_says_so_instead_of_passing_quietly()
    {
        var verdict = Series.Linear("x", [new SeriesPoint(1, 1), new SeriesPoint(2, 2)]);

        Assert.Contains("not judged", verdict.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void Monotonicity_catches_the_same_inversion_more_cheaply()
    {
        var points = new List<SeriesPoint> { new(10, 1598), new(15, 7452), new(20, 3146) };

        var verdict = Series.Monotonic("tiles -> rounds", points);

        Assert.False(verdict.Holds);
        Assert.Contains(verdict.Outliers, p => p.X == 20);
    }

    // The rule the app's own CPU field breaks: one shared sampler whose baseline every caller resets, so the
    // same 0,96-core load reads 4,4% with snapshots on and 0,0% with them off, because that is its first call.
    [Fact]
    public void A_meter_without_a_baseline_reports_nothing_rather_than_zero()
    {
        var meter = new CpuMeter();

        Assert.Null(meter.Percent());
        Assert.Contains("n/a (no baseline)", meter.Line("cpu"), StringComparison.Ordinal);

        meter.Start();

        Assert.NotNull(meter.Percent());
    }

    [Fact]
    public void Two_cpu_meters_do_not_reset_each_others_window()
    {
        var first = new CpuMeter();
        first.Start();
        var second = new CpuMeter();
        second.Start();

        Assert.NotNull(first.Percent());
        Assert.NotNull(second.Percent());
    }
}
