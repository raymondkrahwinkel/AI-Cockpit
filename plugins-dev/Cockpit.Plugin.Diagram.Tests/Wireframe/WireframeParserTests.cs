using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// The language itself: indentation, quoting, modifiers, and what a line that cannot be read has to say about
// itself (AC-871). No Avalonia here — the parser deliberately knows nothing about a UI framework.
public class WireframeParserTests
{
    [Fact]
    public void Indentation_NestsByLevel()
    {
        var result = WireframeParser.Parse("""
            screen "Instellingen"
              row
                button "Opslaan"
              label "Klaar"
            """);

        Assert.Empty(result.Errors);
        var root = Assert.Single(result.Screens);
        Assert.Equal(WireframeNodeKind.Screen, root.Kind);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(WireframeNodeKind.Button, root.Children[0].Children.Single().Kind);
        Assert.Equal(WireframeNodeKind.Label, root.Children[1].Kind);
    }

    [Fact]
    public void Indentation_OfFourSpaces_NestsJustTheSame()
    {
        var result = WireframeParser.Parse("screen \"X\"\n    row\n        button \"Opslaan\"");

        Assert.Empty(result.Errors);
        Assert.Equal(WireframeNodeKind.Button, result.Screens.Single().Children.Single().Children.Single().Kind);
    }

    [Fact]
    public void Indentation_ThatLinesUpWithNothingAbove_IsRefusedOnItsOwnLine()
    {
        var result = WireframeParser.Parse("screen \"X\"\n    row\n  label \"Zwevend\"");

        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.Line);
        Assert.Contains("indentation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tabs_AreRefusedWithTheirLineNumber()
    {
        var result = WireframeParser.Parse("screen \"X\"\n\tlabel \"Tab\"");

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("tabs", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuotedText_KeepsItsSpaces_AndAnEscapedQuoteOfItsOwn()
    {
        var result = WireframeParser.Parse("""
            screen "X"
              label "Zeg \"hallo\" tegen de agent"
            """);

        Assert.Empty(result.Errors);
        Assert.Equal("Zeg \"hallo\" tegen de agent", result.Screens.Single().Children.Single().Text);
    }

    [Fact]
    public void AnUnterminatedQuote_IsRefusedOnItsOwnLine()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  label \"Niet gesloten");

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("quote", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Modifiers_AreKeptInTheOrderTheyWereWritten()
    {
        var result = WireframeParser.Parse("""
            screen "X"
              input "Naam" disabled value:"Raymond" w:2 align:right
            """);

        Assert.Empty(result.Errors);
        var node = result.Screens.Single().Children.Single();
        Assert.Equal(
            new[] { WireframeModifierName.Disabled, WireframeModifierName.Value, WireframeModifierName.W, WireframeModifierName.Align },
            node.Modifiers.Select(modifier => modifier.Name).ToArray());
        Assert.Equal("Raymond", node.ValueOf(WireframeModifierName.Value));
        Assert.Equal<int?>(2, node.WeightOf(WireframeModifierName.W));
        Assert.Equal<WireframeAlignment?>(WireframeAlignment.Right, node.Alignment);
    }

    [Fact]
    public void AnUnknownComponent_IsRefusedOnItsOwnLine_AndTheRestStillParses()
    {
        var result = WireframeParser.Parse("""
            screen "X"
              carousel "Uitgelicht"
              label "Blijft staan"
            """);

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("carousel", error.Message, StringComparison.Ordinal);
        Assert.Equal(WireframeNodeKind.Label, result.Screens.Single().Children.Single().Kind);
    }

    [Fact]
    public void AnUnknownModifier_IsRefusedOnItsOwnLine()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  button \"Opslaan\" bold");

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("bold", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("w:0")]
    [InlineData("w:-1")]
    [InlineData("h:veel")]
    public void AWeightThatIsNotAPositiveNumber_IsRefused(string modifier)
    {
        var result = WireframeParser.Parse($"screen \"X\"\n  label \"A\" {modifier}");

        Assert.Equal(2, Assert.Single(result.Errors).Line);
    }

    [Fact]
    public void AnUnknownAlignment_IsRefused_AndNamesTheOnesThatWork()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  row align:middle");

        var error = Assert.Single(result.Errors);
        Assert.Contains("center", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlagThatIsGivenAValue_IsRefused()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  button \"Opslaan\" primary:true");

        Assert.Equal(2, Assert.Single(result.Errors).Line);
    }

    [Fact]
    public void AModifierThatNeedsAValue_IsRefusedWithout()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  label \"A\" w");

        Assert.Equal(2, Assert.Single(result.Errors).Line);
    }

    [Fact]
    public void TextThatComesAfterAModifier_IsRefused()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  button primary \"Opslaan\"");

        Assert.Equal(2, Assert.Single(result.Errors).Line);
    }

    [Fact]
    public void AWidget_CannotCarryChildren()
    {
        var result = WireframeParser.Parse("""
            screen "X"
              button "Opslaan"
                label "Eronder"
            """);

        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.Line);
        Assert.Contains("button", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceThatDoesNotStartWithAScreen_IsOneClearError()
    {
        var result = WireframeParser.Parse("""
            row
              label "Los"
              label "Ook los"
            """);

        Assert.Empty(result.Screens);
        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.Line);
        Assert.Contains("screen", error.Message, StringComparison.Ordinal);
    }

    // ---- A document of several screens (AC-901) ----

    [Fact]
    public void EveryScreenAtTheLeftMargin_IsAScreenOfItsOwn()
    {
        var result = WireframeParser.Parse("""
            screen "Eerste"
              button "Verder"

            screen "Tweede"
              label "Klaar"

            screen "Derde"
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(["Eerste", "Tweede", "Derde"], result.Screens.Select(screen => screen.Text));
        Assert.Equal(WireframeNodeKind.Button, result.Screens[0].Children.Single().Kind);
        Assert.Equal(WireframeNodeKind.Label, result.Screens[1].Children.Single().Kind);
        Assert.Empty(result.Screens[2].Children);
    }

    [Fact]
    public void ALineAtTheLeftMarginThatIsNotAScreen_IsRefusedWithoutLosingTheScreensBeforeIt()
    {
        var result = WireframeParser.Parse("screen \"Eerste\"\nlabel \"Los\"\nscreen \"Tweede\"");

        Assert.Equal(2, Assert.Single(result.Errors).Line);
        Assert.Equal(["Eerste", "Tweede"], result.Screens.Select(screen => screen.Text));
    }

    [Fact]
    public void AnIdUsedInTwoScreens_IsStillRefused_SoOneIdNamesOneComponent()
    {
        var result = WireframeParser.Parse("screen \"Eerste\"\n  button \"Opslaan\" #save\nscreen \"Tweede\"\n  button \"Opslaan\" #save");

        Assert.Equal(4, Assert.Single(result.Errors).Line);
        Assert.Empty(result.Screens[1].Children);
    }

    [Fact]
    public void AnEmptySource_IsNeitherARootNorAnError()
    {
        var result = WireframeParser.Parse("\n   \n");

        Assert.Empty(result.Screens);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void EveryNode_RemembersTheLineItCameFrom()
    {
        var result = WireframeParser.Parse("""
            screen "X"

              row
                button "Opslaan"
            """);

        Assert.Equal<int?>(1, result.Screens.Single().Line);
        Assert.Equal<int?>(3, result.Screens.Single().Children.Single().Line);
        Assert.Equal<int?>(4, result.Screens.Single().Children.Single().Children.Single().Line);
    }

    // ---- Component ids (AC-906) ----

    [Fact]
    public void AnId_IsReadOffTheLine_AndDoesNotCountAsAModifier()
    {
        var result = WireframeParser.Parse("screen \"X\" #scherm\n  button \"Opslaan\" primary #save");

        Assert.Empty(result.Errors);
        Assert.Equal("scherm", result.Screens.Single().Id);
        var button = result.Screens.Single().Children.Single();
        Assert.Equal("save", button?.Id);
        Assert.Equal(WireframeModifierName.Primary, button?.Modifiers.Single().Name);
    }

    [Fact]
    public void AComponentWithoutAnId_CarriesNone_SoAnUnreferencedSourceStaysPlain()
    {
        Assert.Null(WireframeParser.Parse("screen \"X\"").Screens.Single().Id);
    }

    [Fact]
    public void TheSameIdTwice_IsRefusedOnTheSecondLine_BecauseOneIdMustNameOneComponent()
    {
        var result = WireframeParser.Parse("screen \"X\" #a\n  button \"Opslaan\" #a");

        Assert.Equal(2, Assert.Single(result.Errors).Line);
        Assert.Empty(result.Screens.Single().Children);
    }

    [Theory]
    [InlineData("screen \"X\" #met spatie")]
    [InlineData("screen \"X\" #")]
    [InlineData("screen \"X\" #een/twee")]
    public void AnIdOutsideTheAlphabetItMayUse_IsRefused(string source)
    {
        Assert.NotEmpty(WireframeParser.Parse(source).Errors);
    }

    [Fact]
    public void TwoIdsOnOneLine_AreRefused()
    {
        var result = WireframeParser.Parse("screen \"X\" #een #twee");

        Assert.Equal("A component carries at most one id.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void TextAfterAnId_IsRefused_BecauseTheTextComesDirectlyAfterTheComponent()
    {
        Assert.NotEmpty(WireframeParser.Parse("screen #x \"X\"").Errors);
    }

    // ---- Flows between screens (AC-902) ----

    [Fact]
    public void Goto_ToAnExistingScreen_ParsesAndRoundTrips()
    {
        var source = """
            screen "Aanmelden"
              button "Verder" primary goto:"Dashboard"

            screen "Dashboard"
              label "Welkom"
            """;

        var result = WireframeParser.Parse(source);

        Assert.Empty(result.Errors);
        var button = result.Screens[0].Children.Single();
        Assert.Equal("Dashboard", button.ValueOf(WireframeModifierName.Goto));
        Assert.Equal(source, WireframeWriter.Write(result.Screens));
    }

    [Fact]
    public void Goto_DeclaredBeforeItsTargetScreen_StillResolves_BecauseScreensMayForwardReference()
    {
        var result = WireframeParser.Parse("""
            screen "Aanmelden"
              button "Verder" goto:"Dashboard"

            screen "Dashboard"
            """);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Goto_ToAnUnknownScreen_IsAParseError_ButTheComponentStaysInTheTree()
    {
        var result = WireframeParser.Parse("""
            screen "Aanmelden"
              button "Verder" primary goto:"Onbekend"
            """);

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("Onbekend", error.Message, StringComparison.Ordinal);
        var button = Assert.Single(result.Screens.Single().Children);
        Assert.Equal(WireframeNodeKind.Button, button.Kind);
        Assert.Equal("Onbekend", button.ValueOf(WireframeModifierName.Goto));
    }

    [Fact]
    public void Goto_ToATitleTwoScreensShare_IsAParseError_RatherThanTheFirstWinning()
    {
        var result = WireframeParser.Parse("""
            screen "Aanmelden"
              button "Verder" goto:"Dashboard"

            screen "Dashboard"

            screen "Dashboard"
            """);

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("2 screens", error.Message, StringComparison.Ordinal);
    }

    // ---- Notes (AC-907) ----

    [Fact]
    public void Note_ParsesOnAnyComponent_IncludingTheScreenLine_AndRoundTrips()
    {
        const string source = """
            screen "Aanmelden" note:"desktop only for now"
              input "Wachtwoord" note:"minimaal 12 tekens"
              button "Aanmelden" primary disabled note:"uit tot beide velden gevuld zijn"
            """;

        var result = WireframeParser.Parse(source);

        Assert.Empty(result.Errors);
        var screen = result.Screens.Single();
        Assert.Equal("desktop only for now", screen.ValueOf(WireframeModifierName.Note));
        Assert.Equal("minimaal 12 tekens", screen.Children[0].ValueOf(WireframeModifierName.Note));
        Assert.Equal("uit tot beide velden gevuld zijn", screen.Children[1].ValueOf(WireframeModifierName.Note));
        Assert.Equal(source, WireframeWriter.Write(result.Screens));
    }

    [Fact]
    public void Note_WithoutAValue_IsAParseErrorNamingTheLine()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  button \"Opslaan\" note");

        Assert.Equal(2, Assert.Single(result.Errors).Line);
    }

    [Fact]
    public void Note_WithoutAValue_TheHintFitsATextModifier_NotANumber()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  button \"Opslaan\" note");

        Assert.DoesNotContain("note:2", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteInsideANote_SurvivesBothDirections()
    {
        const string source = """
            screen "X"
              button "Opslaan" note:"toon \"opgeslagen\" na klikken"
            """;

        var result = WireframeParser.Parse(source);

        Assert.Empty(result.Errors);
        Assert.Equal(source, WireframeWriter.Write(result.Screens));
    }

    // ---- Viewport (AC-915) ----

    [Fact]
    public void ASourceWithoutAViewportLine_ReadsAsDesktop()
    {
        var result = WireframeParser.Parse("screen \"X\"");

        Assert.Empty(result.Errors);
        Assert.Null(result.Viewport);
    }

    [Theory]
    [InlineData("desktop", WireframeViewport.Desktop)]
    [InlineData("tablet", WireframeViewport.Tablet)]
    [InlineData("mobile", WireframeViewport.Mobile)]
    public void AViewportLine_AboveTheFirstScreen_ParsesAndRoundTrips(string name, WireframeViewport expected)
    {
        var source = $"viewport {name}\n\nscreen \"X\"";
        var result = WireframeParser.Parse(source);

        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.Viewport);
    }

    [Fact]
    public void AnUnknownViewportName_IsRefusedOnItsOwnLine_ButTheScreenBelowStillRenders()
    {
        var result = WireframeParser.Parse("viewport phablet\n\nscreen \"X\"");

        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.Line);
        Assert.Contains("phablet", error.Message, StringComparison.Ordinal);
        Assert.Contains("desktop, tablet or mobile", error.Message, StringComparison.Ordinal);
        Assert.Null(result.Viewport);
        Assert.Single(result.Screens);
    }

    [Fact]
    public void ASecondViewportLine_IsRefused()
    {
        var result = WireframeParser.Parse("viewport mobile\nviewport tablet\n\nscreen \"X\"");

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("already declares", error.Message, StringComparison.Ordinal);
        Assert.Equal(WireframeViewport.Mobile, result.Viewport);
    }

    [Fact]
    public void AViewportLine_AfterTheFirstScreen_IsRefused()
    {
        var result = WireframeParser.Parse("screen \"X\"\nviewport mobile");

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("above the first screen", error.Message, StringComparison.Ordinal);
        Assert.Null(result.Viewport);
    }

    [Fact]
    public void AnIndentedViewportLine_IsRefused()
    {
        var result = WireframeParser.Parse("  viewport mobile\n\nscreen \"X\"");

        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.Line);
        Assert.Contains("left margin", error.Message, StringComparison.Ordinal);
        Assert.Null(result.Viewport);
    }
}
