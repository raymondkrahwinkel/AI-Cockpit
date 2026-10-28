using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-1880: the operator's wheel through the assistant's transcript, counting painted frames with no row on screen.
// Linked into Cockpit.Journeys, so AssistantJourney scrolls the real window the same way the view test does.
public static class AssistantChatScrolling
{
    /// <summary>
    /// The operator's wheel through the transcript, <paramref name="steps"/> notches up and as many back down; the
    /// painted frames with no row on screen.
    /// </summary>
    public static async Task<int> UpAndDownAsync(AssistantChatView view, int steps)
    {
        var empty = 0;
        for (var step = 0; step < steps; step++)
        {
            empty += await WheelAsync(view, -150);
        }

        for (var step = 0; step < steps; step++)
        {
            empty += await WheelAsync(view, 150);
        }

        return empty;
    }

    // One wheel notch as the operator turns it, then three painted frames: how many of them showed no row.
    public static async Task<int> WheelAsync(AssistantChatView view, double delta)
    {
        if (view.TranscriptScroll is not { } scroll)
        {
            throw new InvalidOperationException("The assistant's transcript has no scroll owner.");
        }

        view.Follower.NoteWheel();
        scroll.Offset = scroll.Offset.WithY(Math.Max(0, scroll.Offset.Y + delta));
        var empty = 0;
        for (var frame = 0; frame < 3; frame++)
        {
            await PumpAsync(1);
            empty += _ShowsARow(view.TranscriptItems, scroll) ? 0 : 1;
        }

        return empty;
    }

    // Whether any realised row overlaps the viewport: the reader sees text, not an empty pane.
    private static bool _ShowsARow(ItemsControl items, ScrollViewer scroll) =>
        items.GetRealizedContainers().Any(row => row.TranslatePoint(default, scroll) is { } top
            && top.Y < scroll.Viewport.Height && top.Y + row.Bounds.Height > 0);

    public static async Task PumpAsync(int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
    }
}
