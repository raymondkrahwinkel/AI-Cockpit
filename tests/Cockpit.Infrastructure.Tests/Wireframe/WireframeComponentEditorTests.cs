using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Infrastructure.Wireframe;

namespace Cockpit.Infrastructure.Tests.Wireframe;

/// <summary>
/// The per-component line surgery behind cockpit-wireframe (AC-872): one component named by its stable id, the rest
/// of the source left exactly as it was, and every change gated on the result still being readable.
/// </summary>
public class WireframeComponentEditorTests
{
    [Fact]
    public void Add_PutsTheComponentInsideTheContainer_AtItsChildrenIndent()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.Group, "input", "Telefoonnummer", null, null));

        Assert.Null(result.Refusal);
        Assert.Equal("        input \"Telefoonnummer\"", WireframeScreens.LineOf(result.Text!, 11));
        Assert.Equal("added input \"Telefoonnummer\"", result.Summary);
    }

    [Fact]
    public void Add_CarriesTheModifiersThroughVerbatim()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.ButtonRow, "button", "Toepassen", "primary w:2", null));

        Assert.Null(result.Refusal);
        Assert.Equal("        button \"Toepassen\" primary w:2", WireframeScreens.LineOf(result.Text!, 14));
    }

    [Fact]
    public void Add_WithAKeywordTheFormatDoesNotHave_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.Group, "textbox", "Naam", null, null));

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
            WireframeComponentEdit.Add(WireframeScreens.ButtonRow, "button", "Toepassen", "bold", null));

        Assert.Null(result.Text);
        Assert.Contains("cannot read", result.Refusal);
    }

    [Fact]
    public void SetText_ChangesTheTextAndKeepsEveryModifier()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetText(WireframeScreens.NameField, "Volledige naam"));

        Assert.Null(result.Refusal);
        Assert.Equal("        input \"Volledige naam\" value:\"Raymond\" #name", WireframeScreens.LineOf(result.Text!, 9));
    }

    [Fact]
    public void SetText_LeavesEveryOtherLineExactlyAsItWas()
    {
        var before = WireframeScreens.LinesOf(WireframeScreens.Settings);

        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));

        // The two halves around the one line that was allowed to change, compared whole — a shorter or a shifted
        // document fails on length before it fails on content.
        var after = WireframeScreens.LinesOf(result.Text!);
        var at = WireframeScreens.SaveButtonLine - 1;
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before[..at], after[..at]);
        Assert.Equal(before[(at + 1)..], after[(at + 1)..]);
    }

    [Fact]
    public void SetText_FoldsAwayALineBreak_SoOneComponentCannotBecomeTwo()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Opslaan\n        button \"Smokkel\""));

        Assert.Null(result.Refusal);
        Assert.Equal(13, WireframeScreens.LinesOf(result.Text!).Length);
        Assert.StartsWith("        button \"Opslaan ", WireframeScreens.LineOf(result.Text!, 13), StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_TakesTheComponentsNestedInsideItWithIt()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Remove(WireframeScreens.LeftColumn));

        Assert.Null(result.Refusal);
        Assert.Equal(9, WireframeScreens.LinesOf(result.Text!).Length);
        Assert.DoesNotContain("nav", result.Text, StringComparison.Ordinal);
        Assert.Contains("3 components inside it", result.Summary);
    }

    [Fact]
    public void Move_ReindentsTheBlockToFitWhereItLands()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.SaveButton, WireframeScreens.Group, position: 0));

        Assert.Null(result.Refusal);
        Assert.Equal("        button \"Opslaan\" primary #save", WireframeScreens.LineOf(result.Text!, 9));
        Assert.Equal(13, WireframeScreens.LinesOf(result.Text!).Length);
    }

    [Fact]
    public void SetModifier_QuotesATextValue_ButNotANumber()
    {
        var quoted = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.EmailField, WireframeModifierName.Value, "raymond@example.com", quoted: true));
        Assert.Contains("value:\"raymond@example.com\"", quoted.Text);

        var numeric = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.NameField, WireframeModifierName.Value, "2", quoted: false));
        Assert.Contains("value:2", numeric.Text);
    }


    // ---- Notes (AC-907) ----




    [Fact]
    public void ChangeType_KeepsThePlaceTheIdTheTextAndTheModifiers()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.ChangeType(WireframeScreens.NameField, "select"));

        Assert.Null(result.Refusal);
        Assert.Equal("        select \"Profielnaam\" value:\"Raymond\" #name", WireframeScreens.LineOf(result.Text!, 9));
        Assert.Equal(13, WireframeScreens.LinesOf(result.Text!).Length);
    }

    [Fact]
    public void AnEditThatChangesNothing_IsRefused_RatherThanJournaledAsAnEmptyStep()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.SaveButton, WireframeScreens.ButtonRow, position: 1));

        Assert.Null(result.Text);
        Assert.Contains("exactly as it is", result.Refusal);
    }

    // ---- A document of several screens (AC-901) ----

    [Fact]
    public void AddScreen_PutsAScreenAtTheLeftMargin_AfterTheOnesAlreadyThere()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.Settings, WireframeComponentEdit.AddScreen("Aanmelden", position: null));

        Assert.Null(result.Refusal);
        var lines = WireframeScreens.LinesOf(result.Text!);
        Assert.Equal("", lines[^2]);
        Assert.Equal("screen \"Aanmelden\"", lines[^1]);
        Assert.Equal("added screen \"Aanmelden\"", result.Summary);
    }

    // ---- Flows between screens (AC-902) ----

    [Fact]
    public void SetText_OnAScreen_CarriesEveryGotoThatPointedAtTheOldTitle_ToTheNewOne()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreensWithFlow,
            WireframeComponentEdit.SetText(WireframeScreens.SignupScreen, "Account aanmaken"));

        Assert.Null(result.Refusal);
        Assert.Contains("screen \"Account aanmaken\" #signup", result.Text, StringComparison.Ordinal);
        Assert.Contains("goto:\"Account aanmaken\"", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("goto:\"Registreren\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 flow", result.Summary);
    }

    [Fact]
    public void Remove_OfAScreenAGotoStillPointsAt_IsRefused_NamingTheScreenAndTheReferrer()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreensWithFlow,
            WireframeComponentEdit.Remove(WireframeScreens.SignupScreen));

        Assert.Null(result.Text);
        Assert.Contains("screen \"Registreren\"", result.Refusal);
        Assert.Contains("button \"Aanmelden\"", result.Refusal);
    }

    [Fact]
    public void SetModifier_Goto_IsAlwaysQuoted_BecauseScreenTitlesCarrySpaces()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreensWithFlow,
            WireframeComponentEdit.SetModifier(WireframeScreens.SignupSubmit, WireframeModifierName.Goto, "Aanmelden", quoted: true));

        Assert.Null(result.Refusal);
        Assert.Contains("goto:\"Aanmelden\"", result.Text, StringComparison.Ordinal);
    }

    // ---- Viewport (AC-915) ----

    [Fact]
    public void SetViewport_OnASourceWithoutOne_InsertsItAboveTheFirstScreen()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.Settings, WireframeComponentEdit.SetViewport(WireframeViewport.Mobile));

        Assert.Null(result.Refusal);
        Assert.Equal("viewport mobile", WireframeScreens.LineOf(result.Text!, 1));
        Assert.Equal("", WireframeScreens.LineOf(result.Text!, 2));
        Assert.Equal("screen \"Instellingen\" #screen", WireframeScreens.LineOf(result.Text!, 3));
        Assert.Equal("set the viewport to mobile", result.Summary);
    }

    [Fact]
    public void SetViewport_OnASourceThatAlreadyDeclaresOne_ReplacesItInPlace()
    {
        var withDesktop = $"viewport desktop\n\n{WireframeScreens.Settings}";

        var result = WireframeComponentEditor.Apply(withDesktop, WireframeComponentEdit.SetViewport(WireframeViewport.Tablet));

        Assert.Null(result.Refusal);
        Assert.Equal("viewport tablet", WireframeScreens.LineOf(result.Text!, 1));
        Assert.Equal(WireframeScreens.LinesOf(withDesktop).Length, WireframeScreens.LinesOf(result.Text!).Length);
    }


    // ---- States (AC-914) ----

    [Fact]
    public void Add_AStateIntoAGroup_IsRefused_AStateOnlyGoesDirectlyUnderItsScreen()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.Group, "state", "Empty", "replaces:#name", null));

        Assert.Null(result.Text);
        Assert.Contains("directly to its screen", result.Refusal);
    }

    [Fact]
    public void Remove_OfTheStateItself_LeavesTheContainerItReplacedInPlace()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.WithState, WireframeComponentEdit.Remove(WireframeScreens.EmptyState));

        Assert.Null(result.Refusal);
        Assert.Contains("list #results", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("state \"Empty\"", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SetModifier_Replaces_RepointsTheState()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.WithState,
            WireframeComponentEdit.SetModifier(WireframeScreens.EmptyState, WireframeModifierName.Replaces, "#main"));

        Assert.Null(result.Refusal);
        Assert.Contains("replaces:#main", result.Text, StringComparison.Ordinal);
    }
}
