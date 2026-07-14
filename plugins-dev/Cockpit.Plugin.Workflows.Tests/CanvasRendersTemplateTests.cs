using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// A flow that opens has to be visible: the canvas drew a template's steps somewhere off-screen, because it kept the
/// pan of whatever flow was open before it. The status line said "4 steps" while the canvas said nothing at all.
/// </summary>
[Collection("avalonia")]
public class CanvasRendersTemplateTests
{
    [Fact]
    public void AFlowsSteps_AreOnTheCanvas()
    {
        var canvas = new WorkflowCanvas(_Flow());

        var cards = canvas.GetVisualDescendants().OfType<WorkflowNodeControl>().ToList();

        cards.Should().HaveCount(2, "both steps of the flow are drawn");
    }

    // The whole point of the fit: the steps end up inside the viewport, whatever the canvas was looking at before.
    [Fact]
    public void AfterFitting_TheStepsAreInsideTheViewport()
    {
        var canvas = new WorkflowCanvas(_Flow());
        var window = new Window { Width = 900, Height = 600, Content = canvas };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        canvas.FitToContent(new Size(900, 600));
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var canvasBounds = new Rect(canvas.Bounds.Size);
        foreach (var card in canvas.GetVisualDescendants().OfType<WorkflowNodeControl>())
        {
            var topLeft = card.TranslatePoint(new Point(0, 0), canvas);
            topLeft.Should().NotBeNull();
            canvasBounds.Contains(topLeft!.Value).Should().BeTrue($"'{card.Node.Name}' must be somewhere the operator can see it");
        }

        window.Close();
    }

    private static Workflow _Flow()
    {
        var flow = new Workflow { Id = "w", Name = "Ticket → branch → agent" };
        flow.Nodes.Add(new WorkflowNode { Id = "a", TypeId = "cockpit.manual", Name = "Start", X = 80, Y = 160 });
        flow.Nodes.Add(new WorkflowNode { Id = "b", TypeId = "cockpit.command", Name = "Cut the branch", X = 920, Y = 160 });
        flow.Connect("a", 0, "b");

        return flow;
    }
}
