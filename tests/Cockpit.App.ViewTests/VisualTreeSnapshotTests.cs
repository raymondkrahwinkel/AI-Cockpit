using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The verify loop (AC-86) feeds this snapshot back to every provider as text, so what it records has to be exact and
/// on the UI thread — a control only has bounds after it is laid out. These cover the facts an agent verifies with it:
/// resolved colours, text content, subtree targeting, and that invisible chrome stays out.
/// </summary>
[Collection("avalonia")]
public class VisualTreeSnapshotTests
{
    // A named pill with a colour, a rounded border and text, plus a hidden sibling to prove it gets skipped.
    private static void WithTree(Action<Window> body) => HeadlessAvalonia.Run(() =>
    {
        var root = new StackPanel
        {
            Children =
            {
                new Border
                {
                    Name = "Pill",
                    Background = new SolidColorBrush(Color.FromRgb(0x13, 0x15, 0x19)),
                    CornerRadius = new CornerRadius(11),
                    Width = 90,
                    Height = 20,
                    Child = new TextBlock { Text = "82%", Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xB2, 0x5A)) },
                },
                new TextBlock { Text = "HIDDEN_MARKER", IsVisible = false },
            },
        };

        var window = new Window { Content = root, Width = 220, Height = 120 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            body(window);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Capture_ResolvesBrushesTextAndCorner_AndLeavesHiddenChromeOut() => WithTree(window =>
    {
        var snapshot = VisualTreeSnapshot.Capture(window);

        Assert.Contains("bg=#131519", snapshot);
        Assert.Contains("corner=11", snapshot);
        Assert.Contains("\"82%\"", snapshot);
        Assert.Contains("fg=#D9B25A", snapshot);
        Assert.DoesNotContain("HIDDEN_MARKER", snapshot);
    });

    [Theory]
    [InlineData("Pill", "Border \"Pill\"")]
    // No control is named "TextBlock", so the type fallback must scope to a TextBlock subtree.
    [InlineData("TextBlock", "TextBlock")]
    public void Capture_TargetsANamedSubtree_OrFallsBackToAControlType(string target, string expectedStart) =>
        WithTree(window =>
        {
            var snapshot = VisualTreeSnapshot.Capture(window, target);

            Assert.StartsWith(expectedStart, snapshot);
            // The subtree comes with its own children, or an agent verifying a pill reads an empty box.
            Assert.Contains("\"82%\"", snapshot);
        });

    [Fact]
    public void Capture_NotesAMissingTarget() => WithTree(window =>
        Assert.Contains("no control named or typed \"Nope\"", VisualTreeSnapshot.Capture(window, "Nope")));
}
