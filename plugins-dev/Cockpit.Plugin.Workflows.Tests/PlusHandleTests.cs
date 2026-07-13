using Avalonia.Controls;
using Avalonia.Input;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The <c>+</c> on a way out has to be draggable, and this test exists because the first version was not: it was a
/// <see cref="Button"/>, and a Button marks the pointer press as handled in its own class handler — before any
/// handler you add to it. So the drag never started, and when the click handler moved out of the way, the + did
/// nothing at all. The bug was invisible in the code and obvious in the hand.
/// </summary>
[Collection("avalonia")]
public class PlusHandleTests
{
    [Fact]
    public void APressOnThePlus_ReachesTheHandler_UnlikeAButton()
    {
        var pin = _Pin();
        var handle = new PlusHandle(pin);

        var pressed = 0;
        handle.Pressed += (_, _) => pressed++;

        handle.RaiseEvent(_Press());

        pressed.Should().Be(1, "the canvas starts drawing a wire on this press — if it never arrives, the + is dead");
    }

    [Fact]
    public void AButton_SwallowsThePress_WhichIsWhyThePlusIsNotOne()
    {
        // The failing design, kept as a test: a Button handles the press itself, so a handler added afterwards
        // never sees an unhandled event. Anyone tempted to "simplify" the + back into a Button meets this.
        var button = new Button();

        var seenUnhandled = 0;
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.Handled)
            {
                seenUnhandled++;
            }
        });

        button.RaiseEvent(_Press());

        seenUnhandled.Should().Be(0);
    }

    [Fact]
    public void ThePlus_KnowsWhichWayOutItBelongsTo()
    {
        var pin = _Pin();

        new PlusHandle(pin).Pin.Should().BeSameAs(pin);
    }

    private static WorkflowPin _Pin()
    {
        var node = new WorkflowNode { Id = "n", TypeId = "cockpit.notify", Name = "Notify" };
        return new WorkflowPin(new WorkflowNodeControl(node), isInput: false, outputIndex: 0);
    }

    private static PointerPressedEventArgs _Press() => new(
        new Panel(),
        new Pointer(0, PointerType.Mouse, isPrimary: true),
        new Panel(),
        default,
        0,
        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
        KeyModifiers.None);
}
