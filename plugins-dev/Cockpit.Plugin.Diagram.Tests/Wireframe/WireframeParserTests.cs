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
        var root = result.Root;
        Assert.NotNull(root);
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
        Assert.Equal(WireframeNodeKind.Button, result.Root?.Children.Single().Children.Single().Kind);
    }

    [Fact]
    public void Indentation_ThatLinesUpWithNothingAbove_IsRefusedOnItsOwnLine()
    {
        var result = WireframeParser.Parse("screen \"X\"\n    row\n  label \"Zwevend\"");

        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.Line);
        Assert.Contains("inspringing", error.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("Zeg \"hallo\" tegen de agent", result.Root?.Children.Single().Text);
    }

    [Fact]
    public void AnUnterminatedQuote_IsRefusedOnItsOwnLine()
    {
        var result = WireframeParser.Parse("screen \"X\"\n  label \"Niet gesloten");

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("aanhalingsteken", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Modifiers_AreKeptInTheOrderTheyWereWritten()
    {
        var result = WireframeParser.Parse("""
            screen "X"
              input "Naam" disabled value:"Raymond" w:2 align:right
            """);

        Assert.Empty(result.Errors);
        var node = result.Root?.Children.Single();
        Assert.NotNull(node);
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
        Assert.Equal(WireframeNodeKind.Label, result.Root?.Children.Single().Kind);
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

        Assert.Null(result.Root);
        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.Line);
        Assert.Contains("screen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondScreen_IsRefusedWithoutLosingTheFirst()
    {
        var result = WireframeParser.Parse("screen \"Eerste\"\nscreen \"Tweede\"");

        Assert.Equal("Eerste", result.Root?.Text);
        Assert.Equal(2, Assert.Single(result.Errors).Line);
    }

    [Fact]
    public void AnEmptySource_IsNeitherARootNorAnError()
    {
        var result = WireframeParser.Parse("\n   \n");

        Assert.Null(result.Root);
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

        Assert.Equal<int?>(1, result.Root?.Line);
        Assert.Equal<int?>(3, result.Root?.Children.Single().Line);
        Assert.Equal<int?>(4, result.Root?.Children.Single().Children.Single().Line);
    }

    // ---- Component ids (AC-906) ----

    [Fact]
    public void AnId_IsReadOffTheLine_AndDoesNotCountAsAModifier()
    {
        var result = WireframeParser.Parse("screen \"X\" #scherm\n  button \"Opslaan\" primary #save");

        Assert.Empty(result.Errors);
        Assert.Equal("scherm", result.Root?.Id);
        var button = result.Root?.Children.Single();
        Assert.Equal("save", button?.Id);
        Assert.Equal(WireframeModifierName.Primary, button?.Modifiers.Single().Name);
    }

    [Fact]
    public void AComponentWithoutAnId_CarriesNone_SoAnUnreferencedSourceStaysPlain()
    {
        Assert.Null(WireframeParser.Parse("screen \"X\"").Root?.Id);
    }

    [Fact]
    public void TheSameIdTwice_IsRefusedOnTheSecondLine_BecauseOneIdMustNameOneComponent()
    {
        var result = WireframeParser.Parse("screen \"X\" #a\n  button \"Opslaan\" #a");

        Assert.Equal(2, Assert.Single(result.Errors).Line);
        Assert.Empty(result.Root!.Children);
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

        Assert.Equal("Een component draagt hoogstens \u00E9\u00E9n id.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void TextAfterAnId_IsRefused_BecauseTheTextComesDirectlyAfterTheComponent()
    {
        Assert.NotEmpty(WireframeParser.Parse("screen #x \"X\"").Errors);
    }
}
