using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-981: saveStatus ("No file yet") and hint ("Click a component to edit it.") sit next to each other in the same
// WrapPanel, same secondary color, with only ItemSpacing between them — without a visible mark they read as one
// nonsense sentence.
[Collection("avalonia")]
public class StatusHintSeparationTests
{
    [Fact]
    public void Wireframe_WithNoFileAndNoSelection_SeparatorSitsVisiblyBetweenStatusAndHint()
    {
        var body = new WireframeWorkspaceBody(new ActivityStripTests.FakeHost(), WireframeDocument.New("Test wireframe"), null);
        var window = _Show(body, width: 960, height: 680);

        var saveStatus = _Field<TextBlock>(body, "_saveStatus");
        var hint = _Field<TextBlock>(body, "_handHint");
        var separator = _Field<TextBlock>(body, "_hintSeparator");

        Assert.Equal("No file yet", saveStatus.Text);
        Assert.Equal("Click a component to edit it.", hint.Text);
        Assert.True(separator.IsVisible);
        Assert.False(string.IsNullOrEmpty(separator.Text));

        _AssertSeparatorBetween(window, saveStatus, separator, hint);

        window.Close();
    }

    [Fact]
    public void Diagram_WithNothingSelected_SeparatorIsHiddenBecauseHintIsEmpty()
    {
        // AC-981: the separator only makes sense between two texts — with nothing selected and no connection in
        // progress, DiagramWorkspaceBody's hint is genuinely empty, so a bare "No file yet ·" trailing dot would be
        // worse than the overlap this ticket fixes.
        var body = new DiagramWorkspaceBody(new ActivityStripTests.FakeHost(), DiagramDocument.New("Test diagram"), null);
        var window = _Show(body, width: 900, height: 640);

        var hint = _Field<TextBlock>(body, "_handHint");
        var separator = _Field<TextBlock>(body, "_hintSeparator");

        Assert.Equal("", hint.Text);
        Assert.False(separator.IsVisible);

        window.Close();
    }

    [Fact]
    public void Diagram_WhileConnecting_SeparatorSitsVisiblyBetweenStatusAndHint()
    {
        var body = new DiagramWorkspaceBody(new ActivityStripTests.FakeHost(), DiagramDocument.New("Test diagram"), null);
        var window = _Show(body, width: 900, height: 640);

        var connect = body.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Connect"));
        _Click(connect);
        Dispatcher.UIThread.RunJobs();

        var saveStatus = _Field<TextBlock>(body, "_saveStatus");
        var hint = _Field<TextBlock>(body, "_handHint");
        var separator = _Field<TextBlock>(body, "_hintSeparator");

        Assert.False(string.IsNullOrEmpty(hint.Text));
        Assert.True(separator.IsVisible);

        _AssertSeparatorBetween(window, saveStatus, separator, hint);

        window.Close();
    }

    // Proves the fix at the pixel level rather than just at the string level: a reader scans left to right, so the
    // separator's rendered rect must land strictly between the two texts, not overlap either one.
    private static void _AssertSeparatorBetween(Window window, TextBlock before, TextBlock separator, TextBlock after)
    {
        var beforeRect = _RectInWindow(window, before);
        var separatorRect = _RectInWindow(window, separator);
        var afterRect = _RectInWindow(window, after);

        Assert.False(beforeRect.Intersects(separatorRect), "status text overlaps the separator");
        Assert.False(separatorRect.Intersects(afterRect), "separator overlaps the hint text");
        Assert.True(separatorRect.X >= beforeRect.Right, "separator does not sit to the right of the status text");
        Assert.True(afterRect.X >= separatorRect.Right, "hint does not sit to the right of the separator");
    }

    private static Rect _RectInWindow(Window window, Control control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException($"{control.GetType().Name} must be laid out to be checked");
        return new Rect(topLeft, control.Bounds.Size);
    }

    private static T _Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    // Buttons in these toolbars are wired via Click, not Command — raise the routed event directly.
    private static void _Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Window _Show(Control content, double width, double height)
    {
        var window = new Window { Content = content, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
