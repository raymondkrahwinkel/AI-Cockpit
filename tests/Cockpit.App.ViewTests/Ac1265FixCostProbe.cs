using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

// AC-1265 scratch probe. Not a claim: it prices the two fix routes and always passes.
[Collection("avalonia")]
public sealed class Ac1265FixCostProbe
{
    private const string ReadingsPath = "/tmp/ac1265/cost.txt";

    private static void _Report(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReadingsPath)!);
        File.AppendAllText(ReadingsPath, line + Environment.NewLine);
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

    private static int _Realised(ItemsControl items)
    {
        var realised = 0;
        for (var i = 0; i < items.ItemCount; i++)
        {
            if (items.ContainerFromIndex(i) is not null)
            {
                realised++;
            }
        }

        return realised;
    }

    // Route B priced: the panel swapped for a plain StackPanel, which is the only way to take the estimate out
    // of the picture without reimplementing the panel. 300 rows is a working afternoon's transcript.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RouteB_TurningVirtualisationOff(bool virtualising)
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            for (var i = 0; i < 300; i++)
            {
                vm.Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.AssistantText,
                    string.Join(' ', Enumerable.Repeat($"row {i} body text", 4 + (i * 37 % 120)))));
            }

            var view = new SessionView { DataContext = vm };
            if (!virtualising)
            {
                view.TranscriptItems.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
            }

            var window = new Window { Content = view, Width = 900, Height = 600 };

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalMemory(true);
            var started = DateTime.UtcNow;

            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, TimeSpan.FromMilliseconds(600));

            var elapsed = DateTime.UtcNow - started;
            var after = GC.GetTotalMemory(false);
            var realised = _Realised(view.TranscriptItems);

            _Report(
                $"routeB/{(virtualising ? "virtualising" : "plain-stackpanel")}: "
                + $"{realised} of {view.TranscriptItems.ItemCount} rows built, "
                + $"first layout {elapsed.TotalMilliseconds:F0}ms, managed heap +{(after - before) / 1024.0 / 1024.0:F1}MB");

            window.Close();
        });
    }

    // Does route B actually steady the thumb? Pricing a fix that does not work is worth nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RouteB_DoesItSteadyTheThumb(bool virtualising)
    {
        var thumbs = new List<double>();

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            for (var i = 0; i < 12; i++)
            {
                vm.Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.AssistantText,
                    string.Join(' ', Enumerable.Repeat($"row {i} body text", 4 + (i * 37 % 240)))));
            }

            var view = new SessionView { DataContext = vm };
            if (!virtualising)
            {
                view.TranscriptItems.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
            }

            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, TimeSpan.FromMilliseconds(300));

            var scroll = view.TranscriptScroll!;
            void Sample() => thumbs.Add(Ac1265ThumbLengthProbe.ThumbHeight(scroll));

            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here it is:\n\n```csharp\n" });
            for (var i = 0; i < 40; i++)
            {
                vm.Apply(new AssistantTextDelta
                {
                    SessionId = "S1",
                    BlockIndex = 0,
                    Text = $"    var line{i} = ComputeSomethingWithARatherLongName(argument{i}, other{i});\n\n",
                });
                await _PumpAsync(Sample, TimeSpan.FromMilliseconds(25));
            }

            await _PumpAsync(Sample, TimeSpan.FromMilliseconds(400));
            window.Close();
        });

        var reversals = 0;
        for (var i = 2; i < thumbs.Count; i++)
        {
            if ((thumbs[i] - thumbs[i - 1]) * (thumbs[i - 1] - thumbs[i - 2]) < 0)
            {
                reversals++;
            }
        }

        _Report(
            $"routeB-thumb/{(virtualising ? "virtualising" : "plain-stackpanel")}: "
            + $"{reversals} reversals over {thumbs.Count} frames, {thumbs.Min():F0}..{thumbs.Max():F0}px");
    }

    // Route A priced by looking: a fenced code block whole, split at a line boundary the naive way, and split
    // with the fence re-opened on the second row. Iron Law #9 -- this one is decided by the picture, not by a number.
    [Theory]
    [InlineData("whole")]
    [InlineData("split-naive")]
    [InlineData("split-refenced")]
    public async Task RouteA_WhatASplitCodeBlockLooksLike(string shape)
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var head = "```csharp\nvar first = ComputeSomething(argument, other);\nvar second = ComputeSomethingElse(argument);";
            var tail = "var third = AndAnotherOne(argument, other);\nvar fourth = TheLastLine(argument);\n```";

            var vm = new SessionViewModel();
            vm.Transcript.Clear();
            vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Here is the change:"));
            switch (shape)
            {
                case "whole":
                    vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, head + "\n" + tail));
                    break;
                case "split-naive":
                    vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, head));
                    // IsReplyContinuation is what _OpenAssistantRow sets on a second row of one reply, so the
                    // name and badge are suppressed the way the real split path already suppresses them.
                    vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, tail) { IsReplyContinuation = true });
                    break;
                default:
                    vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, head + "\n```"));
                    vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "```csharp\n" + tail) { IsReplyContinuation = true });
                    break;
            }

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 760, Height = 460 };
            window.Show();
            window.UpdateLayout();
            await _PumpAsync(null, TimeSpan.FromMilliseconds(700));

            var frame = window.CaptureRenderedFrame();
            Directory.CreateDirectory("/tmp/ac1265");
            if (frame is not null)
            {
                frame.Save($"/tmp/ac1265/routeA-{shape}.png", PngBitmapEncoderOptions.Default);
                _Report($"routeA/{shape}: wrote /tmp/ac1265/routeA-{shape}.png");
            }
            else
            {
                _Report($"routeA/{shape}: no frame captured");
            }

            window.Close();
        });
    }
}
