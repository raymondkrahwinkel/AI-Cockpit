using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// Opening a step's settings (#69). The gesture is a double-click, and this test exists because the obvious way to
/// implement it does not work here: the first click starts a drag and captures the pointer, and a captured pointer
/// never delivers Avalonia's <c>DoubleTapped</c>. So the second click is read from the press itself. The settings
/// panel was there for a whole release, and unreachable.
/// </summary>
[Collection("avalonia")]
public class NodeCardTests
{
    [Fact]
    public void ADoubleClickOnAStep_OpensIt()
    {
        var card = new WorkflowNodeControl(_Node());

        var opened = 0;
        card.Opened += (_, _) => opened++;

        _Click(card, clickCount: 2);

        opened.Should().Be(1);
    }

    [Fact]
    public void ASingleClick_DoesNotOpenIt_BecauseThatIsHowYouDragAStep()
    {
        var card = new WorkflowNodeControl(_Node());

        var opened = 0;
        var dragged = 0;
        card.Opened += (_, _) => opened++;
        card.HeaderPressed += (_, _) => dragged++;

        _Click(card, clickCount: 1);

        opened.Should().Be(0);
        dragged.Should().Be(1);
    }

    private static WorkflowNode _Node() => new()
    {
        Id = "n",
        TypeId = "cockpit.command",
        Name = "Run a command",
    };

    // The card listens on its inner surface, so the press is raised on the control that actually carries the
    // handler — which is what the canvas does too.
    private static void _Click(WorkflowNodeControl card, int clickCount)
    {
        var inner = card.GetVisualDescendants().OfType<Border>().First();

        inner.RaiseEvent(new PointerPressedEventArgs(
            inner,
            new Pointer(0, PointerType.Mouse, isPrimary: true),
            inner,
            default,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None,
            clickCount));
    }
}
