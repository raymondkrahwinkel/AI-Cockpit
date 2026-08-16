namespace Cockpit.Plugin.Diagram.Tests;

// AC-834 DoD: single-instance per *document*, never per session. The key is what SurfaceWindows folds on, so this
// is where "same diagram comes forward, two diagrams are two windows" is decided.
public class DiagramWindowTests
{
    [Fact]
    public void KeyFor_IsTheSameForOneDocumentAndDifferentForAnother()
    {
        var one = new DiagramDocument("/memory/Diagrams/architecture.md", "Architecture", "flowchart LR\nA-->B");
        var two = new DiagramDocument("/memory/Diagrams/dataflow.md", "Dataflow", "flowchart LR\nC-->D");

        // The same document opened twice from two different sessions still keys on the document alone.
        Assert.Equal(DiagramWindow.KeyFor(one.Id), DiagramWindow.KeyFor(one.Id));
        Assert.NotEqual(DiagramWindow.KeyFor(one.Id), DiagramWindow.KeyFor(two.Id));
    }

    [Fact]
    public void New_GivesEveryUnsavedDiagramItsOwnIdentity()
    {
        // Two quick-starts with the same name are two diagrams, so they must not collapse into one window.
        Assert.NotEqual(DiagramDocument.New("Nieuw diagram").Id, DiagramDocument.New("Nieuw diagram").Id);
    }

    [Fact]
    public void New_OpensEmpty_NotWithTheAC809Sample()
    {
        // AC-840: a snelstart diagram opens with the snelstart name and no content — the AC-809 sample is now
        // only reached through the panel's explicit "Voorbeeld invoegen" action.
        var document = DiagramDocument.New("Mijn diagram");

        Assert.Equal("Mijn diagram", document.Title);
        Assert.Equal(DiagramDocument.Empty, document.MermaidText);
        Assert.NotEqual(DiagramDocument.Sample, document.MermaidText);
    }
}
