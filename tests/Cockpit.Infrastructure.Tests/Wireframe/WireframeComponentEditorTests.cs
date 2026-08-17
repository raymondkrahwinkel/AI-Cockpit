using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Infrastructure.Wireframe;

namespace Cockpit.Infrastructure.Tests.Wireframe;

/// <summary>
/// The per-component line surgery behind cockpit-wireframe (AC-872): one component named by its line number, the
/// rest of the source left exactly as it was, and every change gated on the result still being readable.
/// </summary>
public class WireframeComponentEditorTests
{
    [Fact]
    public void Add_PutsTheComponentInsideTheContainer_AtItsChildrenIndent()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.GroupLine, "input", "Telefoonnummer", null, null));

        Assert.Null(result.Refusal);
        Assert.Equal("        input \"Telefoonnummer\"", WireframeScreens.LineOf(result.Text!, 11));
        Assert.Equal("added input \"Telefoonnummer\"", result.Summary);
    }

    [Fact]
    public void Add_AtAPosition_PutsTheComponentBeforeThatChild()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.GroupLine, "label", "Persoonlijk", null, position: 0));

        Assert.Null(result.Refusal);
        Assert.Equal("        label \"Persoonlijk\"", WireframeScreens.LineOf(result.Text!, 9));
        Assert.Equal("        input \"Profielnaam\" value:\"Raymond\"", WireframeScreens.LineOf(result.Text!, 10));
    }

    [Fact]
    public void Add_CarriesTheModifiersThroughVerbatim()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.ButtonRowLine, "button", "Toepassen", "primary w:2", null));

        Assert.Null(result.Refusal);
        Assert.Equal("        button \"Toepassen\" primary w:2", WireframeScreens.LineOf(result.Text!, 14));
    }

    [Fact]
    public void Add_IntoAWidget_IsRefused_BecauseAWidgetCarriesNoComponents()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.NameFieldLine, "label", "Hint", null, null));

        Assert.Null(result.Text);
        Assert.Contains("carries no components", result.Refusal);
    }

    [Fact]
    public void Add_WithAKeywordTheFormatDoesNotHave_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.GroupLine, "textbox", "Naam", null, null));

        Assert.Null(result.Text);
        Assert.Contains("not a component this format has", result.Refusal);
    }

    [Fact]
    public void Add_WithAModifierTheFormatDoesNotHave_IsRefused_ByTheReReadGate()
    {
        // Nothing checks the modifier by name: the composed line is written, the whole document is parsed again, and
        // an edit that made a line unreadable is thrown away rather than handed to the operator.
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.ButtonRowLine, "button", "Toepassen", "bold", null));

        Assert.Null(result.Text);
        Assert.Contains("cannot read", result.Refusal);
    }

    [Fact]
    public void Add_OnALineThatHoldsNoComponent_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(99, "button", "Toepassen", null, null));

        Assert.Null(result.Text);
        Assert.Contains("no component on line 99", result.Refusal);
    }

    [Fact]
    public void SetText_ChangesTheTextAndKeepsEveryModifier()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetText(WireframeScreens.NameFieldLine, "Volledige naam"));

        Assert.Null(result.Refusal);
        Assert.Equal("        input \"Volledige naam\" value:\"Raymond\"", WireframeScreens.LineOf(result.Text!, 9));
    }

    [Fact]
    public void SetText_LeavesEveryOtherLineExactlyAsItWas()
    {
        var before = WireframeScreens.LinesOf(WireframeScreens.Settings);

        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetText(WireframeScreens.SaveButtonLine, "Bewaren"));

        var after = WireframeScreens.LinesOf(result.Text!);
        Assert.Equal(before.Length, after.Length);
        for (var line = 0; line < before.Length; line++)
        {
            if (line != WireframeScreens.SaveButtonLine - 1)
            {
                Assert.Equal(before[line], after[line]);
            }
        }
    }

    [Fact]
    public void SetText_FoldsAwayALineBreak_SoOneComponentCannotBecomeTwo()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetText(WireframeScreens.SaveButtonLine, "Opslaan\n        button \"Smokkel\""));

        Assert.Null(result.Refusal);
        Assert.Equal(13, WireframeScreens.LinesOf(result.Text!).Length);
        Assert.StartsWith("        button \"Opslaan ", WireframeScreens.LineOf(result.Text!, 13), StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_TakesTheComponentsNestedInsideItWithIt()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Remove(WireframeScreens.LeftColumnLine));

        Assert.Null(result.Refusal);
        Assert.Equal(9, WireframeScreens.LinesOf(result.Text!).Length);
        Assert.DoesNotContain("nav", result.Text, StringComparison.Ordinal);
        Assert.Contains("3 components inside it", result.Summary);
    }

    [Fact]
    public void Remove_OfTheScreenLine_IsRefused_BecauseThatIsTheWireframeItself()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.Settings, WireframeComponentEdit.Remove(1));

        Assert.Null(result.Text);
        Assert.Contains("the wireframe itself", result.Refusal);
    }

    [Fact]
    public void Move_ReindentsTheBlockToFitWhereItLands()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.SaveButtonLine, WireframeScreens.GroupLine, position: 0));

        Assert.Null(result.Refusal);
        Assert.Equal("        button \"Opslaan\" primary", WireframeScreens.LineOf(result.Text!, 9));
        Assert.Equal(13, WireframeScreens.LinesOf(result.Text!).Length);
    }

    [Fact]
    public void Move_ReordersWithinTheSameContainer()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.SaveButtonLine, WireframeScreens.ButtonRowLine, position: 0));

        Assert.Null(result.Refusal);
        Assert.Equal("        button \"Opslaan\" primary", WireframeScreens.LineOf(result.Text!, 12));
        Assert.Equal("        button \"Annuleren\"", WireframeScreens.LineOf(result.Text!, 13));
    }

    [Fact]
    public void Move_IntoItself_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.RowLine, WireframeScreens.GroupLine, null));

        Assert.Null(result.Text);
        Assert.Contains("cannot be moved inside itself", result.Refusal);
    }

    [Fact]
    public void Move_IntoAWidget_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.SaveButtonLine, WireframeScreens.EmailFieldLine, null));

        Assert.Null(result.Text);
        Assert.Contains("carries no components", result.Refusal);
    }

    [Fact]
    public void AnEditThatChangesNothing_IsRefused_RatherThanJournaledAsAnEmptyStep()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.SaveButtonLine, WireframeScreens.ButtonRowLine, position: 1));

        Assert.Null(result.Text);
        Assert.Contains("exactly as it is", result.Refusal);
    }
}
