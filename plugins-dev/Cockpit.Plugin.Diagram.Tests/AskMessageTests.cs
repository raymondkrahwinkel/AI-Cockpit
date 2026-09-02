using Cockpit.Plugin.Diagram.Collab;

namespace Cockpit.Plugin.Diagram.Tests;

public class AskMessageTests
{
    [Fact]
    public void Compose_WithAnObject_NamesTheSurfaceAndTheObject()
    {
        var context = new AskContext("diagram", "d1", "Login flow", "A", "Login");

        var text = AskMessage.Compose(context, "rename this node");

        Assert.Equal("🗨️ Ask the agent · diagram \"Login flow\" (id d1) · A — Login — rename this node", text);
    }

    [Fact]
    public void Compose_WithoutAnObject_NamesOnlyTheSurface()
    {
        var context = new AskContext("wireframe", "wf1", "Onboarding", ObjectRef: null, ObjectLabel: null);

        var text = AskMessage.Compose(context, "what should this screen do next?");

        Assert.Equal("🗨️ Ask the agent · wireframe \"Onboarding\" (id wf1) — what should this screen do next?", text);
    }

    [Fact]
    public void Compose_WithAnObjectLabelContainingALineBreak_FoldsItToOneLine()
    {
        var context = new AskContext("diagram", "d1", "Flow", "A", "Line one\nLine two");

        var text = AskMessage.Compose(context, "explain this");

        Assert.DoesNotContain('\n', text);
        Assert.Contains("A — Line one Line two", text);
    }

    // AC-910 criterion 4: the object reference must be one the agent can address, never just a title (criterion 3).
    // Compose_WithAnObject_NamesTheSurfaceAndTheObject asserts the whole composed line for a diagram; this is the
    // one combination it does not cover — a second surface kind, carrying an object.
    [Fact]
    public void Compose_ForWireframe_IncludesTheComponentIdAndTheScreenTitle()
    {
        var context = new AskContext("wireframe", "wf1", "Checkout", "#c7", "on screen \"Sign up\"");

        var text = AskMessage.Compose(context, "make this primary");

        Assert.Contains("#c7", text);
        Assert.Contains("Sign up", text);
    }

    [Fact]
    public void Compose_ForWhiteboard_IncludesKindAndTextAndBoardRect_NeverAGuid()
    {
        var context = new AskContext(
            "whiteboard",
            "wb1",
            "Brainstorm",
            ObjectRef: null,
            ObjectLabel: "StickyNote reading \"Payment\" around (820, 460), 160×90");

        var text = AskMessage.Compose(context, "what does this mean?");

        Assert.Contains("StickyNote", text);
        Assert.Contains("Payment", text);
        Assert.Contains("820", text);
        Assert.Contains("460", text);
        Assert.DoesNotMatch(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", text);
    }

    [Fact]
    public void Compose_TheOperatorsQuestion_GoesVerbatim()
    {
        var context = new AskContext("diagram", "d1", "Flow", ObjectRef: null, ObjectLabel: null);

        var text = AskMessage.Compose(context, "line one\nline two — keep this exactly as typed");

        Assert.EndsWith("line one\nline two — keep this exactly as typed", text);
    }
}
