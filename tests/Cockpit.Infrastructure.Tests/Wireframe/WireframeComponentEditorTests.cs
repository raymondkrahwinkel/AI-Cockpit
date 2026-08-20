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
    public void Add_AtAPosition_PutsTheComponentBeforeThatChild()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.Group, "label", "Persoonlijk", null, position: 0));

        Assert.Null(result.Refusal);
        Assert.Equal("        label \"Persoonlijk\"", WireframeScreens.LineOf(result.Text!, 9));
        Assert.Equal("        input \"Profielnaam\" value:\"Raymond\" #name", WireframeScreens.LineOf(result.Text!, 10));
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
    public void Add_IntoAWidget_IsRefused_BecauseAWidgetCarriesNoComponents()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add(WireframeScreens.NameField, "label", "Hint", null, null));

        Assert.Null(result.Text);
        Assert.Contains("carries no components", result.Refusal);
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
    public void Add_UnderAnIdThatNamesNothing_IsRefused_RatherThanLandingSomewhereElse()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Add("no-such-id", "button", "Toepassen", null, null));

        Assert.Null(result.Text);
        Assert.Contains("no component with id \"no-such-id\"", result.Refusal);
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
    public void Remove_OfTheOnlyScreen_IsRefused_BecauseThatIsTheWireframeItself()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.Settings, WireframeComponentEdit.Remove(WireframeScreens.Screen));

        Assert.Null(result.Text);
        Assert.Contains("only screen", result.Refusal);
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
    public void Move_ReordersWithinTheSameContainer()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.SaveButton, WireframeScreens.ButtonRow, position: 0));

        Assert.Null(result.Refusal);
        Assert.Equal("        button \"Opslaan\" primary #save", WireframeScreens.LineOf(result.Text!, 12));
        Assert.Equal("        button \"Annuleren\" #cancel", WireframeScreens.LineOf(result.Text!, 13));
    }

    [Fact]
    public void Move_IntoItself_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.Row, WireframeScreens.Group, null));

        Assert.Null(result.Text);
        Assert.Contains("cannot be moved inside itself", result.Refusal);
    }

    [Fact]
    public void Move_IntoAWidget_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.Move(WireframeScreens.SaveButton, WireframeScreens.EmailField, null));

        Assert.Null(result.Text);
        Assert.Contains("carries no components", result.Refusal);
    }

    [Fact]
    public void ToggleModifier_On_AddsTheFlagAtTheEndOfTheLine()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.ToggleModifier(WireframeScreens.AccountItem, WireframeModifierName.Selected, on: true));

        Assert.Null(result.Refusal);
        Assert.Equal("        item \"Account\" selected #account", WireframeScreens.LineOf(result.Text!, 6));
    }

    [Fact]
    public void ToggleModifier_Off_RemovesTheFlagAndLeavesTheRestOfTheLine()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.ToggleModifier(WireframeScreens.GeneralItem, WireframeModifierName.Selected, on: false));

        Assert.Null(result.Refusal);
        Assert.Equal("        item \"Algemeen\" #general", WireframeScreens.LineOf(result.Text!, 5));
    }

    [Fact]
    public void ToggleModifier_WithNoMeaningOnThisComponent_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.ToggleModifier(WireframeScreens.NameField, WireframeModifierName.Primary, on: true));

        Assert.Null(result.Text);
        Assert.Contains("no meaning", result.Refusal);
    }

    [Fact]
    public void SetModifier_UpdatesAnExistingValueInPlace_RatherThanMovingItToTheEnd()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.SaveButton, WireframeModifierName.Align, "left"));

        // "Opslaan" already carries `primary` before the id — align is new here, so it lands at the end, but the
        // existing modifier keeps its own place rather than being reordered around it.
        Assert.Null(result.Refusal);
        Assert.Equal("        button \"Opslaan\" primary align:left #save", WireframeScreens.LineOf(result.Text!, 13));
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

    [Fact]
    public void SetModifier_WithAnEmptyValue_ClearsTheModifierInstead()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.NameField, WireframeModifierName.Value, null));

        Assert.Null(result.Refusal);
        Assert.Equal("        input \"Profielnaam\" #name", WireframeScreens.LineOf(result.Text!, 9));
    }

    [Fact]
    public void SetModifier_WNotOnARowHeaderOrFooterChild_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.Group, WireframeModifierName.W, "2"));

        Assert.Null(result.Text);
        Assert.Contains("no meaning", result.Refusal);
    }

    [Fact]
    public void SetModifier_UnderAnIdThatNamesNothing_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier("no-such-id", WireframeModifierName.Align, "left"));

        Assert.Null(result.Text);
        Assert.Contains("no component with id \"no-such-id\"", result.Refusal);
    }

    // ---- Notes (AC-907) ----

    [Fact]
    public void SetModifier_Note_AddsTheModifierQuoted()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.SaveButton, WireframeModifierName.Note, "disabled until valid", quoted: true));

        Assert.Null(result.Refusal);
        Assert.Equal("        button \"Opslaan\" primary note:\"disabled until valid\" #save", WireframeScreens.LineOf(result.Text!, 13));
    }

    [Fact]
    public void SetModifier_Note_UpdatesAnExistingNoteInPlace()
    {
        var once = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.SaveButton, WireframeModifierName.Note, "first version", quoted: true));

        var twice = WireframeComponentEditor.Apply(
            once.Text!,
            WireframeComponentEdit.SetModifier(WireframeScreens.SaveButton, WireframeModifierName.Note, "second version", quoted: true));

        Assert.Null(twice.Refusal);
        Assert.Equal("        button \"Opslaan\" primary note:\"second version\" #save", WireframeScreens.LineOf(twice.Text!, 13));
    }

    [Fact]
    public void SetModifier_Note_WithAnEmptyValue_ClearsIt()
    {
        var withNote = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.SaveButton, WireframeModifierName.Note, "disabled until valid", quoted: true));

        var cleared = WireframeComponentEditor.Apply(
            withNote.Text!,
            WireframeComponentEdit.SetModifier(WireframeScreens.SaveButton, WireframeModifierName.Note, null));

        Assert.Null(cleared.Refusal);
        Assert.Equal("        button \"Opslaan\" primary #save", WireframeScreens.LineOf(cleared.Text!, 13));
    }

    [Fact]
    public void Remove_TakesTheComponentsNoteWithIt_BecauseItStandsOnTheSameLine()
    {
        var withNote = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetModifier(WireframeScreens.SaveButton, WireframeModifierName.Note, "disabled until valid", quoted: true));

        var removed = WireframeComponentEditor.Apply(withNote.Text!, WireframeComponentEdit.Remove(WireframeScreens.SaveButton));

        Assert.Null(removed.Refusal);
        Assert.DoesNotContain("disabled until valid", removed.Text, StringComparison.Ordinal);
    }

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
    public void ChangeType_ToAWidget_WhenItStillHasChildren_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.ChangeType(WireframeScreens.Group, "label"));

        Assert.Null(result.Text);
        Assert.Contains("carries no components of its own", result.Refusal);
    }

    [Fact]
    public void ChangeType_ToAKeywordTheFormatDoesNotHave_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.ChangeType(WireframeScreens.NameField, "textbox"));

        Assert.Null(result.Text);
        Assert.Contains("not a component this format has", result.Refusal);
    }

    [Fact]
    public void ChangeType_OfTheScreenLine_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.ChangeType(WireframeScreens.Screen, "row"));

        Assert.Null(result.Text);
        Assert.Contains("cannot be changed", result.Refusal);
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

    [Fact]
    public void AddScreen_AtAPosition_PutsItBeforeThatScreen()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.TwoScreens, WireframeComponentEdit.AddScreen("Welkom", position: 0));

        Assert.Null(result.Refusal);
        Assert.Equal("screen \"Welkom\"", WireframeScreens.LineOf(result.Text!, 1));
        Assert.Equal("screen \"Aanmelden\" #login", WireframeScreens.LineOf(result.Text!, 3));
    }

    [Fact]
    public void AddScreen_WithoutATitle_IsRefused_BecauseThatIsWhatNamesItInTheOverview()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.Settings, WireframeComponentEdit.AddScreen(" ", position: null));

        Assert.Null(result.Text);
        Assert.Contains("title", result.Refusal);
    }

    [Fact]
    public void Remove_OfOneScreen_LeavesTheOtherScreenExactlyAsItWas()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.TwoScreens, WireframeComponentEdit.Remove(WireframeScreens.SignupScreen));

        Assert.Null(result.Refusal);
        Assert.Contains("screen \"Aanmelden\" #login", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Registreren", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_IntoTheSecondScreen_LeavesTheFirstOneUntouched()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreens,
            WireframeComponentEdit.Add(WireframeScreens.SignupScreen, "checkbox", "Voorwaarden", null, null));

        Assert.Null(result.Refusal);
        var lines = WireframeScreens.LinesOf(result.Text!);
        Assert.Equal(WireframeScreens.LinesOf(WireframeScreens.TwoScreens)[..4], lines[..4]);
        Assert.Equal("  checkbox \"Voorwaarden\"", lines[^1]);
    }

    [Fact]
    public void Move_OfAScreen_IsRefused_BecauseAScreenStandsAtTheLeftMarginOfItsOwn()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreens,
            WireframeComponentEdit.Move(WireframeScreens.SignupScreen, WireframeScreens.LoginScreen, position: null));

        Assert.Null(result.Text);
        Assert.Contains("left margin", result.Refusal);
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
    public void SetText_OnAScreenNoFlowPointsAt_MentionsNoFlows()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreensWithFlow,
            WireframeComponentEdit.SetText(WireframeScreens.LoginScreen, "Inloggen"));

        Assert.Null(result.Refusal);
        Assert.DoesNotContain("flow", result.Summary);
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
    public void Remove_OfAScreenNoGotoPointsAt_Succeeds()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreensWithFlow,
            WireframeComponentEdit.Remove(WireframeScreens.LoginScreen));

        Assert.Null(result.Refusal);
        Assert.DoesNotContain("Aanmelden", result.Text, StringComparison.Ordinal);
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

    [Fact]
    public void SetViewport_ToTheOneAlreadyInEffect_IsRefused_AsANoOpChange()
    {
        var withMobile = $"viewport mobile\n\n{WireframeScreens.Settings}";

        var result = WireframeComponentEditor.Apply(withMobile, WireframeComponentEdit.SetViewport(WireframeViewport.Mobile));

        Assert.Null(result.Text);
        Assert.Contains("exactly as it is", result.Refusal);
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
    public void Add_AStateOnItsScreen_WithReplaces_Succeeds()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.WithState,
            WireframeComponentEdit.Add(WireframeScreens.Screen, "state", "Loading", "replaces:#results", null));

        Assert.Null(result.Refusal);
        Assert.Contains("state \"Loading\" replaces:#results", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeType_OfAState_IsRefused()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.WithState, WireframeComponentEdit.ChangeType(WireframeScreens.EmptyState, "card"));

        Assert.Null(result.Text);
        Assert.Contains("A state's type cannot be changed", result.Refusal);
    }

    [Fact]
    public void ChangeType_IntoAState_IsRefused_AddComponentMakesOneInstead()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.WithState, WireframeComponentEdit.ChangeType(WireframeScreens.Main, "state"));

        Assert.Null(result.Text);
        Assert.Contains("cannot be changed into a state", result.Refusal);
    }

    [Fact]
    public void Remove_OfAContainerAStateStillReplaces_IsRefused_NamingTheState()
    {
        var result = WireframeComponentEditor.Apply(WireframeScreens.WithState, WireframeComponentEdit.Remove(WireframeScreens.Results));

        Assert.Null(result.Text);
        Assert.Contains("state \"Empty\"", result.Refusal);
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
    public void Move_AStateIntoAnotherContainer_IsRefused_AStateStaysOnItsOwnScreen()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.WithState,
            WireframeComponentEdit.Move(WireframeScreens.EmptyState, WireframeScreens.Main, position: null));

        Assert.Null(result.Text);
        Assert.Contains("stays directly under its own screen", result.Refusal);
    }

    [Fact]
    public void Move_AStateWithinItsOwnScreen_Succeeds_BecauseItIsOnlyReordering()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.WithState,
            WireframeComponentEdit.Move(WireframeScreens.EmptyState, WireframeScreens.Screen, position: 0));

        Assert.Null(result.Refusal);
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
