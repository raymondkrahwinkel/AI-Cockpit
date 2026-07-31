using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-528 criterion 7: "mouse wheel over a tool result that does not itself scroll must scroll the transcript."
/// SessionView.axaml's code-result box (the codeBox ScrollViewer, MaxHeight 280) does not set
/// VerticalScrollBarVisibility="Disabled" the way MarkdownView.cs's code blocks do — the candidate this measures.
/// Avalonia's own ScrollContentPresenter chains an unhandled wheel to the parent whenever the inner offset does not
/// change (IsScrollChainingEnabled defaults to true; a ScrollViewer with nothing to scroll never changes offset),
/// so the prediction is that this already works without a code change. Measured directly rather than assumed.
/// </summary>
[Collection("avalonia")]
public class Ac528NestedResultWheelTests
{
    private static void _Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static (Window Window, ScrollViewer Transcript, ScrollViewer CodeBox) _PaneWithAnExpandedResult(string resultText)
    {
        var session = new SessionViewModel { ReadingLevel = ReadingLevel.Developer };
        session.Transcript.Clear();

        var tool = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "ran something")
        {
            ToolName = "Bash", ToolUseId = "t1", InputJson = "{}", IsExpanded = true,
        };
        tool.SetResult(resultText, isError: false);
        session.Transcript.Add(tool);

        // Enough filler rows below that the transcript itself has real room to scroll — otherwise a wheel event
        // over the code box would report "not handled" for the trivial reason that the transcript itself has
        // nothing to scroll either, which would not prove chaining at all.
        for (var index = 0; index < 40; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var window = new Window { Width = 700, Height = 420, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        var transcriptScroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "TranscriptScroll");

        // AC-528's stick-to-bottom guard now latches off real input, not scroll deltas — a raw ScrollToHome() call
        // here is not "the operator" and gets auto-resnapped straight back to the bottom by that same guard
        // (_stickToBottom starts true). A real wheel-up tick is what a reading operator actually does, so it is
        // what has to be simulated to leave the bottom and stay there.
        var homePoint = new Point(transcriptScroll.Bounds.Width / 2, transcriptScroll.Bounds.Height / 2);
        for (var i = 0; i < 300 && transcriptScroll.Offset.Y > 0; i++)
        {
            window.MouseWheel(homePoint, new Vector(0, 1), RawInputModifiers.None);
            _Settle(window);
        }

        // Scope to this row specifically: the composer's own multiline TextBox has a ScrollViewer in its Fluent
        // control template too, and it sits earlier in the visual tree than the transcript's rows (the composer is
        // declared before the transcript Grid in SessionView.axaml) — a plain "first ScrollViewer other than the
        // transcript's" picks that one up instead of the code box.
        var toolRow = window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("transcriptRow") && ReferenceEquals(b.DataContext, tool));
        var codeBox = toolRow.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.IsEffectivelyVisible);

        return (window, transcriptScroll, codeBox);
    }

    // Raises a wheel event on the code box's own ScrollContentPresenter (its template's actual wheel handler,
    // Avalonia.Controls.Presenters.ScrollContentPresenter — the ScrollViewer control itself does not implement
    // OnPointerWheelChanged) rather than simulating screen coordinates. Raising on the ScrollViewer control
    // instead would bubble straight past its own handling to the ancestors, which does not exercise chaining at
    // all — the point of this event is to start exactly where a real wheel-over-content event starts.
    private static void _RaiseWheel(Window window, ScrollViewer codeBox)
    {
        var presenter = (Control)codeBox.GetVisualDescendants().First(v => v.GetType().Name == "ScrollContentPresenter");
        var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
        var args = new PointerWheelEventArgs(
            presenter, pointer, window, new Point(codeBox.Bounds.Width / 2, codeBox.Bounds.Height / 2),
            (ulong)Environment.TickCount64,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None, new Vector(0, -3))
        {
            RoutedEvent = InputElement.PointerWheelChangedEvent,
        };
        presenter.RaiseEvent(args);
        _Settle(window);
    }

    [Fact]
    public void WheelOverAShortResultThatDoesNotScroll_ScrollsTheTranscriptInstead() => HeadlessAvalonia.Run(() =>
    {
        // Multi-line so ResultIsCodeLike is true (renders in the codeBox ScrollViewer), short enough that it fits
        // well inside MaxHeight="280" — nothing for the inner ScrollViewer to scroll.
        var (window, transcriptScroll, codeBox) = _PaneWithAnExpandedResult("line 1\nline 2\nline 3\nline 4\nline 5");

        var offsetBefore = transcriptScroll.Offset.Y;
        _RaiseWheel(window, codeBox);

        Assert.True(codeBox.Offset.Y == 0, "the code box has nothing to scroll, so its own offset must not move");
        Assert.True(transcriptScroll.Offset.Y > offsetBefore,
            "AC-528 criterion 7: a wheel over a tool result that does not itself scroll must scroll the transcript");

        window.Close();
    });

    /// <summary>
    /// Contrast case, so the pass above cannot be a false positive from a test-setup mistake: a result long enough
    /// to overflow MaxHeight="280" has real content for the inner ScrollViewer to scroll, and the first wheel tick
    /// must move it rather than the transcript — ordinary nested-scroll chaining, unrelated to AC-528's bug.
    /// </summary>
    [Fact]
    public void WheelOverATallResultThatDoesScroll_ScrollsItselfFirst() => HeadlessAvalonia.Run(() =>
    {
        var longResult = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"result line {i}"));
        var (window, transcriptScroll, codeBox) = _PaneWithAnExpandedResult(longResult);

        var transcriptOffsetBefore = transcriptScroll.Offset.Y;
        _RaiseWheel(window, codeBox);

        Assert.True(codeBox.Offset.Y > 0, "the code box has real overflow, so the wheel must scroll it");
        Assert.Equal(transcriptOffsetBefore, transcriptScroll.Offset.Y);

        window.Close();
    });
}
