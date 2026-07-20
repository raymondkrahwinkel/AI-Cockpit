using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAssertions;

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
    public void Capture_ResolvesBrushesTextAndCorner() => WithTree(window =>
    {
        var snapshot = VisualTreeSnapshot.Capture(window);

        snapshot.Should().Contain("bg=#131519");
        snapshot.Should().Contain("corner=11");
        snapshot.Should().Contain("\"82%\"");
        snapshot.Should().Contain("fg=#D9B25A");
    });

    [Fact]
    public void Capture_SkipsHiddenSubtrees() => WithTree(window =>
        VisualTreeSnapshot.Capture(window).Should().NotContain("HIDDEN_MARKER"));

    [Fact]
    public void Capture_TargetsANamedSubtree() => WithTree(window =>
    {
        var snapshot = VisualTreeSnapshot.Capture(window, "Pill");

        snapshot.Should().StartWith("Border \"Pill\"");
        snapshot.Should().Contain("\"82%\"");
    });

    [Fact]
    public void Capture_TargetsByControlType_WhenNoNameMatches() => WithTree(window =>
        // No control is named "TextBlock", so the type fallback must scope to a TextBlock subtree.
        VisualTreeSnapshot.Capture(window, "TextBlock").Should().StartWith("TextBlock"));

    [Fact]
    public void Capture_NotesAMissingTarget() => WithTree(window =>
        VisualTreeSnapshot.Capture(window, "Nope").Should().Contain("no control named or typed \"Nope\""));
}
