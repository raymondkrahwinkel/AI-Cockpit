using NodeEditor.Model;
using NodeEditor.Mvvm;

namespace NodeEditorSpike;

/// <summary>
/// The actual question this spike asks: can a cockpit workflow — a trigger node feeding an action node — be
/// expressed in this library's model, saved, and loaded back? Two nodes with typed pins and one connection is
/// the smallest thing that answers it; if this cannot round-trip, no amount of canvas polish matters.
/// </summary>
internal static class WorkflowGraph
{
    private const double PinSize = 8;

    public static IDrawingNode Build()
    {
        var drawing = new DrawingNodeViewModel
        {
            Width = 900,
            Height = 600,
            Nodes = new System.Collections.ObjectModel.ObservableCollection<INode>(),
            Connectors = new System.Collections.ObjectModel.ObservableCollection<IConnector>(),
        };

        // A trigger has no input: something outside the flow starts it.
        var trigger = _Node("Trigger: pr.opened", x: 60, y: 80, out var triggerOut, inputs: 0, outputs: 1);

        // An action has one input and one output, so it can be chained.
        var notify = _Node("Action: notify", x: 420, y: 80, out var notifyOut, inputs: 1, outputs: 1);

        drawing.Nodes!.Add(trigger);
        drawing.Nodes!.Add(notify);

        var notifyIn = notify.Pins!.First();
        drawing.Connectors!.Add(new ConnectorViewModel
        {
            Parent = drawing,
            Start = triggerOut,
            End = notifyIn,
        });

        _ = notifyOut;
        return drawing;
    }

    private static INode _Node(string label, double x, double y, out IPin outputPin, int inputs, int outputs)
    {
        const double width = 220;
        const double height = 70;

        var node = new NodeViewModel
        {
            Name = label,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Pins = new System.Collections.ObjectModel.ObservableCollection<IPin>(),
            Content = label,
        };

        for (var index = 0; index < inputs; index++)
        {
            node.Pins!.Add(new PinViewModel
            {
                Parent = node,
                X = 0,
                Y = height / (inputs + 1) * (index + 1),
                Width = PinSize,
                Height = PinSize,
                Alignment = PinAlignment.Left,
            });
        }

        IPin? last = null;
        for (var index = 0; index < outputs; index++)
        {
            last = new PinViewModel
            {
                Parent = node,
                X = width,
                Y = height / (outputs + 1) * (index + 1),
                Width = PinSize,
                Height = PinSize,
                Alignment = PinAlignment.Right,
            };
            node.Pins!.Add(last);
        }

        outputPin = last ?? throw new InvalidOperationException($"'{label}' has no output pin to connect from.");
        return node;
    }
}
