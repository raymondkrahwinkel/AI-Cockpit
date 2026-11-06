using Cockpit.MeasurementHarness.Core;
using Cockpit.MeasurementHarness.Meters;

namespace Cockpit.App.ViewTests;

// AC-1131: the decision functions of the measurement harness, which are the part that has to be right for
// any of its numbers to mean anything. They need no window and no compositor, so they run in CI — the rest
// of the harness does not, and says so (see the harness README, "the CI boundary").
public sealed class MeasurementHarnessTests
{
    private static RunIdentity _Identity(string[]? args = null, string sha = "7d331771") =>
        RunIdentity.Capture("test", args ?? ["--a=1"], new Dictionary<string, string> { ["a"] = "1" }, () => sha);

    private static IReadOnlyList<IReadOnlyList<SeriesPoint>> _Passes(params double[][] passes) =>
        passes.Select(pass => (IReadOnlyList<SeriesPoint>)pass.Select((rounds, index) => new SeriesPoint(index + 2, rounds)).ToList()).ToList();

    [Fact]
    public void Magnitude_check_rejects_the_single_pass_collapse_that_the_median_allows()
    {
        var passes = _Passes(
            [6, 9, 11, 159, 23],
            [6, 9, 11, 159, 159],
            [6, 9, 11, 159, 159]);

        Assert.True(Series.Monotonic("sessions -> median worst frame rounds", Series.MedianByX(passes.SelectMany(pass => pass).ToList())).Holds);
        Assert.False(Series.Magnitude("sessions -> worst frame rounds per pass", passes, 0.25).Holds);
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

    // AC-1104: the cut-off frames have to be priced against the healthy frames of the same run, not against
    // the whole run. Averaging the two together is what hid an 800 ms frame among 200 frames of 12 ms.
    [Fact]
    public void The_cut_off_frames_are_priced_apart_from_the_healthy_ones()
    {
        // Frame 1 took 900 ms over 150 rounds; frames 2 and 3 took 10 ms over 2 rounds each.
        var costs = new List<FrameCost> { new(1, 900, 300_000_000), new(2, 10, 2_000_000), new(3, 10, 2_000_000) };
        var ordinals = Enumerable.Repeat(1, 150).Concat([2, 2, 3, 3]).ToList();

        var cost = FrameCostSummary.Of(costs, ordinals, 100);

        Assert.NotNull(cost);
        Assert.Equal(1, cost.Frames);
        Assert.Equal(3, cost.FramesTotal);
        Assert.Equal(900, cost.LongestMs);
        Assert.Equal(10, cost.AverageOtherFrameMs);
        Assert.Equal(2_000_000, cost.AllocatedBytesPerRound);
        Assert.Equal(1_000_000, cost.OtherAllocatedBytesPerRound);
    }
}
