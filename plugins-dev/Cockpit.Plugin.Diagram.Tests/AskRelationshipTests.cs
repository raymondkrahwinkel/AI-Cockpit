using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-975: an empty label used to make "Connect" in the ER relationship flyout return silently — no relationship,
// no toast, no explanation. This opens the real flyout (via the private method the two-click connect gesture
// calls) and checks it refuses to submit silently while the label is empty.
[Collection("avalonia")]
public class AskRelationshipTests
{
    [Fact]
    public void AskRelationship_WithTheLabelLeftEmpty_ConfirmStaysDisabledWithAReason()
    {
        var registry = new DiagramAccessRegistry();
        var document = DiagramDocument.New("Test ER diagram", ErSource);
        var body = new DiagramWorkspaceBody(new ToolbarOverflowTests.DiagramRegistryHost(registry), document, null);
        var window = new Window { Content = body, Width = 900, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        typeof(DiagramWorkspaceBody).GetMethod("_AskRelationship", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(body, ["CUSTOMER", "ORDER"]);
        Dispatcher.UIThread.RunJobs();

        var toolbarConnect = (Button)typeof(DiagramWorkspaceBody).GetField("_connectButton", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(body)!;
        var confirm = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Connect") && b != toolbarConnect);
        Assert.False(confirm.IsEnabled);
        Assert.NotNull(ToolTip.GetTip(confirm));

        window.Close();
    }

    private const string ErSource = """
        erDiagram
            CUSTOMER ||--o{ ORDER : "places"
        """;
}
