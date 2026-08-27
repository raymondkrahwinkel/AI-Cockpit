using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

// AC-1104 measurement scaffold — not a guard, and it goes out again. It answers one question:
// does a ScrollChanged raised while the transcript streams carry an extent delta? If it does,
// _OnTranscriptScrollChanged clears _followCorrected on it and AC-1113's limit never engages.
[Collection("avalonia")]
public sealed class Ac1104ExtentDeltaProbeTests(ITestOutputHelper output)
{
    private static void _Frame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void Probe_StreamingRows_ScrollChangedDeltas() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < 60; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var view = new SessionView { DataContext = session };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        _Frame();
        view.TranscriptScroll.ScrollToEnd();
        _Frame();

        var total = 0;
        var ownCorrection = 0;
        var extentOnly = 0;
        view.TranscriptScroll.ScrollChanged += (_, e) =>
        {
            total++;
            if (Math.Abs(e.ExtentDelta.Y) < 0.5 && Math.Abs(e.ViewportDelta.Y) < 0.5)
            {
                ownCorrection++;
            }
            else if (Math.Abs(e.ViewportDelta.Y) < 0.5)
            {
                extentOnly++;
            }
        };

        // Rows of differing height, because a uniform list keeps the panel's extent estimate still and that
        // is exactly the case AC-1113's test assumes.
        for (var index = 0; index < 40; index++)
        {
            var text = index % 3 == 0
                ? $"streamed {index} — " + string.Join(' ', Enumerable.Repeat("a longer reply that wraps", 12))
                : $"streamed {index}";
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, text));
            _Frame();
        }

        window.Close();

        output.WriteLine($"ScrollChanged total={total} ownCorrection={ownCorrection} extentMoved={extentOnly}");
        Assert.True(total > 0, "no ScrollChanged was raised at all — the probe measured nothing");
    });

    [Fact]
    public void Probe_TallRows_PerRowScrollChangedCount() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < 60; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var view = new SessionView { DataContext = session };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        _Frame();
        view.TranscriptScroll.ScrollToEnd();
        _Frame();

        var perRow = 0;
        var offsetMoved = 0;
        var ownCorrection = 0;
        view.TranscriptScroll.ScrollChanged += (_, e) =>
        {
            perRow++;
            if (Math.Abs(e.OffsetDelta.Y) >= 0.5)
            {
                offsetMoved++;
            }

            if (Math.Abs(e.ExtentDelta.Y) < 0.5 && Math.Abs(e.ViewportDelta.Y) < 0.5)
            {
                ownCorrection++;
            }
        };

        // A row taller than the viewport: the case _FollowNewest documents as the one its shortfall correction
        // exists for, and the one AC-1113 says leaves _NewestRowIsFullyVisible false.
        var tall = string.Join('\n', Enumerable.Range(0, 60).Select(line => $"line {line} of a very tall reply"));
        for (var index = 0; index < 12; index++)
        {
            perRow = 0;
            offsetMoved = 0;
            ownCorrection = 0;
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, tall));
            _Frame();
            output.WriteLine($"tall row {index}: scrollChanged={perRow} offsetMoved={offsetMoved} ownCorrection={ownCorrection}");
        }

        window.Close();
    });
}
