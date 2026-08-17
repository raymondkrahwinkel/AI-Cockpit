using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;

namespace Cockpit.Plugin.Diagram.Tests;

[Collection("avalonia")]
public class PinStripTests
{
    // Reuses ActivityStripTests's fakes (AC-849 grew the same interfaces those already stand in for) rather than
    // writing a second pair for the same shape.
    private static Window _Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static List<string?> _Texts(Control content) =>
        content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();

    private static void _RaiseClick(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void NoPinsYet_ShowsTheExplicitEmptyMessage_NeverABlankStrip()
    {
        var host = new ActivityStripTests.FakeHost(new ActivityStripTests.FakeDiagramRegistry());
        var strip = new PinStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);

        Assert.Contains("Nog geen pins.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void SeededPin_ForThisSurface_ShowsTheQuestion()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        registry.Seed("surface-1", new DiagramPin("p1", "N1", "krijgt de agent te horen waaróm?", DateTime.Now, Closed: false));
        var host = new ActivityStripTests.FakeHost(registry);
        var strip = new PinStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);

        var texts = _Texts(strip);
        Assert.Contains("krijgt de agent te horen waaróm?", texts);
        Assert.DoesNotContain("Nog geen pins.", texts);

        window.Close();
    }

    [Fact]
    public void SeededPin_ForADifferentSurface_IsNotShown()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        registry.Seed("surface-2", new DiagramPin("p1", "N1", "andere vraag", DateTime.Now, Closed: false));
        var host = new ActivityStripTests.FakeHost(registry);
        var strip = new PinStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);

        Assert.Contains("Nog geen pins.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void WhiteboardPin_ProducesAReadableRow()
    {
        var registry = new ActivityStripTests.FakeWhiteboardRegistry();
        registry.Seed("board-1", new WhiteboardPin("p1", "obj-1", "wat betekent dit vak?", DateTime.Now, Closed: false));
        var host = new ActivityStripTests.FakeHost(whiteboard: registry);
        var strip = new PinStrip(host, "board-1", whiteboard: true, null);
        var window = _Show(strip);

        Assert.Contains("wat betekent dit vak?", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void CloseButton_OnAnOpenPin_CallsClosePin_AndTheRowShowsClosed()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        registry.Seed("surface-1", new DiagramPin("p1", "N1", "krijgt de agent te horen waaróm?", DateTime.Now, Closed: false));
        var host = new ActivityStripTests.FakeHost(registry);
        var strip = new PinStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);

        var close = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Sluiten"));
        Assert.True(close.IsEnabled);

        _RaiseClick(close);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(registry.Pins("surface-1"), pin => pin.Closed);
        var reClose = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Sluiten"));
        Assert.False(reClose.IsEnabled);
        Assert.Contains(_Texts(strip), text => text is not null && text.Contains("gesloten", StringComparison.Ordinal));

        window.Close();
    }

    [Fact]
    public void CloseButton_OnAnAlreadyClosedPin_IsDisabledUpFront()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        registry.Seed("surface-1", new DiagramPin("p1", "N1", "vraag", DateTime.Now, Closed: true));
        var host = new ActivityStripTests.FakeHost(registry);
        var strip = new PinStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);

        var close = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Sluiten"));
        Assert.False(close.IsEnabled);

        window.Close();
    }
}
