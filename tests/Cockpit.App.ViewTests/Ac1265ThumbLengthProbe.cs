using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Avalonia.VisualTree;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1265 scratch probe. Not a claim: it writes readings to /tmp/ac1265/readings.txt and always passes.
[Collection("avalonia")]
public sealed class Ac1265ThumbLengthProbe
{
    private const string ReadingsPath = "/tmp/ac1265/readings.txt";

    private static void _Report(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReadingsPath)!);
        File.AppendAllText(ReadingsPath, line + Environment.NewLine);
    }

    // The height the vertical ScrollBar's own Thumb was laid out at, so the claim rests on the drawn control
    // rather than on the extent it is computed from.
    private static double _ThumbHeight(ScrollViewer scroll) =>
        scroll.GetVisualDescendants()
            .OfType<ScrollBar>()
            .Where(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical)
            .SelectMany(bar => bar.GetVisualDescendants().OfType<Thumb>())
            .Select(thumb => thumb.Bounds.Height)
            .FirstOrDefault();

    private sealed record Sample(double Extent, double Viewport, double Offset, double ThumbHeight)
    {
        public double ThumbLength => Viewport / Math.Max(1.0, Extent);

        public double ThumbPosition => Offset / Math.Max(1.0, Extent - Viewport);
    }

    private static void _Summarise(string label, IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0)
        {
            _Report($"{label}: no samples");

            return;
        }

        var lengths = samples.Select(s => s.ThumbLength).ToList();
        var extents = samples.Select(s => s.Extent).ToList();
        var viewport = samples[0].Viewport;

        var worstLengthStep = 0.0;
        var worstExtentStep = 0.0;
        var lengthReversals = 0;
        var shrinks = 0;
        var worstShrink = 0.0;
        for (var i = 1; i < samples.Count; i++)
        {
            worstLengthStep = Math.Max(worstLengthStep, Math.Abs(lengths[i] - lengths[i - 1]));
            worstExtentStep = Math.Max(worstExtentStep, Math.Abs(extents[i] - extents[i - 1]));
            if (extents[i] < extents[i - 1] - 1.0)
            {
                shrinks++;
                worstShrink = Math.Max(worstShrink, extents[i - 1] - extents[i]);
            }

            if (i >= 2 &&
                Math.Sign(lengths[i] - lengths[i - 1]) != 0 &&
                Math.Sign(lengths[i] - lengths[i - 1]) != Math.Sign(lengths[i - 1] - lengths[i - 2]))
            {
                lengthReversals++;
            }
        }

        var thumbHeights = samples.Select(s => s.ThumbHeight).ToList();
        var worstThumbStep = 0.0;
        for (var i = 1; i < samples.Count; i++)
        {
            worstThumbStep = Math.Max(worstThumbStep, Math.Abs(thumbHeights[i] - thumbHeights[i - 1]));
        }

        _Report(
            $"{label} DRAWN THUMB: {thumbHeights.Min():F0}..{thumbHeights.Max():F0}px, "
            + $"worst single-frame change {worstThumbStep:F0}px");
        _Report($"{label} thumbs: {string.Join(' ', thumbHeights.Select(t => t.ToString("F0")))}");
        _Report(
            $"{label}: frames={samples.Count} viewport={viewport:F0}px "
            + $"extent {extents.Min():F0}..{extents.Max():F0}px "
            + $"thumb-length {lengths.Min() * viewport:F0}..{lengths.Max() * viewport:F0}px of a {viewport:F0}px track, "
            + $"worst single-frame length change {worstLengthStep * viewport:F0}px "
            + $"(extent {worstExtentStep:F0}px), direction reversals {lengthReversals}, "
            + $"extent shrank on {shrinks} frames, worst {worstShrink:F0}px");
        _Report($"{label} extents: {string.Join(' ', extents.Select(e => e.ToString("F0")))}");
    }

    private static async Task _PumpAsync(Action? sample, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            sample?.Invoke();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
    }

    private static TranscriptEntryViewModel _Row(int index, bool mixed)
    {
        // Mixed: heights spread the way a real transcript's do. Uniform is the negative control -- if that one
        // jitters too, the reading is about the instrument rather than about row-height spread.
        var words = mixed ? 4 + (index * 37 % 240) : 40;

        return new TranscriptEntryViewModel(
            TranscriptEntryKind.AssistantText,
            string.Join(' ', Enumerable.Repeat($"row {index} body text", words)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScrollingASettledTranscript(bool mixed)
    {
        var samples = new List<Sample>();
        var label = mixed ? "scroll/mixed-heights" : "scroll/uniform-heights (negative control)";

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            for (var i = 0; i < 200; i++)
            {
                vm.Transcript.Add(_Row(i, mixed: true));
            }

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, TimeSpan.FromMilliseconds(400));

            var scroll = view.TranscriptScroll!;
            view.Follower.StickToBottom = false;

            // Wheel down through the whole transcript the way an operator does, one viewport-ish step at a time.
            for (var step = 0; step < 120; step++)
            {
                scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + 120);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                samples.Add(new Sample(scroll.Extent.Height, scroll.Viewport.Height, scroll.Offset.Y, _ThumbHeight(scroll)));
                await Task.Delay(5);
            }

            window.Close();
        });

        _Summarise(label, samples);
    }

    [Theory]
    [InlineData(false, 900, 600)]
    [InlineData(false, 420, 560)]
    [InlineData(true, 900, 600)]
    [InlineData(true, 420, 560)]
    public async Task StreamingIntoTheSessionPane(bool singleRow, double width, double height)
    {
        var samples = new List<Sample>();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            for (var i = 0; i < 12; i++)
            {
                vm.Transcript.Add(_Row(i, mixed: true));
            }

            var single = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "start of the reply.\n\n");
            if (singleRow)
            {
                vm.Transcript.Add(single);
            }
            else
            {
                vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "start of the reply.\n\n" });
            }

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = width, Height = height };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, TimeSpan.FromMilliseconds(300));

            var scroll = view.TranscriptScroll!;
            void Sample() => samples.Add(
                new Sample(scroll.Extent.Height, scroll.Viewport.Height, scroll.Offset.Y, _ThumbHeight(scroll)));

            for (var i = 0; i < 40; i++)
            {
                var text = $"paragraph {i} of a long markdown answer that keeps growing and wrapping over several lines.\n\n";
                if (singleRow)
                {
                    single.AppendText(text);
                }
                else
                {
                    vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = text });
                }

                await _PumpAsync(Sample, TimeSpan.FromMilliseconds(25));
            }

            await _PumpAsync(Sample, TimeSpan.FromMilliseconds(400));
            window.Close();
        });

        _Summarise(
            $"stream/session-pane/{(singleRow ? "one-tall-row" : "row-per-block")}@{width:F0}x{height:F0}", samples);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingIntoTheAssistantChat(bool singleRow)
    {
        var samples = new List<Sample>();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var session = new SessionViewModel();
            session.Transcript.Clear();
            for (var i = 0; i < 12; i++)
            {
                session.Transcript.Add(_Row(i, mixed: true));
            }

            // Through the session's own event path, not by appending to a row this probe made itself: AC-1238
            // puts the row-per-block split there, so appending direct grows one monster row the app never has.
            var single = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "start of the reply.\n\n");
            if (singleRow)
            {
                session.Transcript.Add(single);
            }
            else
            {
                session.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "start of the reply.\n\n" });
            }

            var host = Substitute.For<IAssistantSessionHost>();
            host.Session.Returns(session);

            var window = new AssistantChatWindow
            {
                Width = 420,
                Height = 560,
                DataContext = new AssistantChatViewModel(
                    host,
                    Substitute.For<IAssistantSettingsStore>(),
                    Substitute.For<IVoicePlaybackQueue>()),
            };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, TimeSpan.FromMilliseconds(300));

            var scroll = window.ChatView.TranscriptScroll!;
            void Sample() => samples.Add(
                new Sample(scroll.Extent.Height, scroll.Viewport.Height, scroll.Offset.Y, _ThumbHeight(scroll)));

            for (var i = 0; i < 40; i++)
            {
                var text = $"paragraph {i} of a long markdown answer that keeps growing and wrapping over several lines.\n\n";
                if (singleRow)
                {
                    single.AppendText(text);
                }
                else
                {
                    session.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = text });
                }

                await _PumpAsync(Sample, TimeSpan.FromMilliseconds(25));
            }

            await _PumpAsync(Sample, TimeSpan.FromMilliseconds(400));
            window.Close();
        });

        _Summarise($"stream/assistant-chat/{(singleRow ? "one-tall-row" : "row-per-block")}@420x560", samples);
    }
}
