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

    // Every way a single line can be refused: the same guarantee with different values — one error, on the line
    // that caused it, saying enough to fix it — so rows, not methods. A refusal that also says something about the
    // tree it left behind is a test of its own below; these rows claim nothing beyond the error itself.
    public static IEnumerable<object[]> RefusedLines() =>
    [
        // ---- Indentation, tabs and quoting ----
        ["screen \"X\"\n    row\n  label \"Zwevend\"", 3, new[] { "indentation" }, Array.Empty<string>()],
        ["screen \"X\"\n\tlabel \"Tab\"", 2, new[] { "tabs" }, Array.Empty<string>()],
        ["screen \"X\"\n  label \"Niet gesloten", 2, new[] { "quote" }, Array.Empty<string>()],

        // ---- Modifiers ----
        ["screen \"X\"\n  button \"Opslaan\" bold", 2, new[] { "bold" }, Array.Empty<string>()],
        // An unknown alignment names the ones that do work, so the author does not have to go looking.
        ["screen \"X\"\n  row align:middle", 2, new[] { "center" }, Array.Empty<string>()],
        // A weight has to be a positive number.
        ["screen \"X\"\n  label \"A\" w:0", 2, Array.Empty<string>(), Array.Empty<string>()],
        ["screen \"X\"\n  label \"A\" w:-1", 2, Array.Empty<string>(), Array.Empty<string>()],
        ["screen \"X\"\n  label \"A\" h:veel", 2, Array.Empty<string>(), Array.Empty<string>()],
        // A flag takes no value, a valued modifier takes one, and text never follows a modifier.
        ["screen \"X\"\n  button \"Opslaan\" primary:true", 2, Array.Empty<string>(), Array.Empty<string>()],
        ["screen \"X\"\n  label \"A\" w", 2, Array.Empty<string>(), Array.Empty<string>()],
        ["screen \"X\"\n  button primary \"Opslaan\"", 2, Array.Empty<string>(), Array.Empty<string>()],
        // A note is text, so the hint must not offer the shape a numeric modifier would take (AC-907).
        ["screen \"X\"\n  button \"Opslaan\" note", 2, Array.Empty<string>(), new[] { "note:2" }],

        // ---- Structure ----
        ["screen \"X\"\n  button \"Opslaan\"\n    label \"Eronder\"", 3, new[] { "button" }, Array.Empty<string>()],

        // ---- Ids (AC-906) ----
        ["screen \"X\" #een #twee", 1, new[] { "A component carries at most one id." }, Array.Empty<string>()],

        // ---- Flows between screens (AC-902) ----
        // A title two screens share is an error rather than the first one quietly winning.
        [
            "screen \"Aanmelden\"\n  button \"Verder\" goto:\"Dashboard\"\n\nscreen \"Dashboard\"\n\nscreen \"Dashboard\"",
            2, new[] { "2 screens" }, Array.Empty<string>(),
        ],
        // A state title is not a screen title, so a goto at it reads as any other unknown screen (AC-914).
        [
            "screen \"X\"\n  list #results\n  button \"Try again\" goto:\"Empty\"\n  state \"Empty\" replaces:#results\n    label \"No results found\"",
            3, new[] { "'Empty' is not a screen in this wireframe." }, Array.Empty<string>(),
        ],

        // ---- States (AC-914) ----
        [
            "screen \"X\"\n  card\n    state \"Empty\" replaces:#c\n      label \"Leeg\"",
            3, new[] { "direct child of a screen" }, Array.Empty<string>(),
        ],
        [
            "screen \"X\"\n  list #results\n  state \"Empty\"\n    label \"No results found\"",
            3, new[] { "replaces:#<id>" }, Array.Empty<string>(),
        ],
        [
            "screen \"X\"\n  state \"Empty\" replaces:#nope\n    label \"No results found\"",
            2, new[] { "#nope", "not a component of this screen" }, Array.Empty<string>(),
        ],
        // Only a container has content to stand in for.
        [
            "screen \"X\"\n  label \"Naam\" #name\n  state \"Empty\" replaces:#name\n    label \"No results found\"",
            3, new[] { "is not a container" }, Array.Empty<string>(),
        ],
        [
            "screen \"X\" #screen\n  state \"Empty\" replaces:#screen\n    label \"No results found\"",
            2, new[] { "not the screen itself" }, Array.Empty<string>(),
        ],
        [
            "screen \"X\"\n  list #results\n  state \"Empty\" replaces:#results #a\n    label \"No results found\"\n  state \"AlsoEmpty\" replaces:#a\n    label \"Nothing here either\"",
            5, new[] { "not the screen itself" }, Array.Empty<string>(),
        ],
        // Ids are document-unique, but a state stays on its own screen.
        [
            "screen \"First\"\n  list #results\n\nscreen \"Second\"\n  state \"Empty\" replaces:#results\n    label \"No results found\"",
            5, new[] { "not a component of this screen" }, Array.Empty<string>(),
        ],
    ];

    [Theory]
    [MemberData(nameof(RefusedLines))]
    public void ALineThatCannotBeRead_IsOneErrorOnItsOwnLine_SayingEnoughToFixIt(
        string source, int line, string[] present, string[] absent)
    {
        var result = WireframeParser.Parse(source);

        var error = Assert.Single(result.Errors);
        Assert.Equal(line, error.Line);
        Assert.All(present, fragment => Assert.Contains(fragment, error.Message, StringComparison.OrdinalIgnoreCase));
        Assert.All(absent, fragment => Assert.DoesNotContain(fragment, error.Message, StringComparison.OrdinalIgnoreCase));
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
            """.ReplaceLineEndings("\n");

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

    // ---- Notes (AC-907) ----

    [Fact]
    public void Note_ParsesOnAnyComponent_IncludingTheScreenLine_AndRoundTrips()
    {
        var source = """
            screen "Aanmelden" note:"desktop only for now"
              input "Wachtwoord" note:"minimaal 12 tekens"
              button "Aanmelden" primary disabled note:"uit tot beide velden gevuld zijn"
            """.ReplaceLineEndings("\n");

        var result = WireframeParser.Parse(source);

        Assert.Empty(result.Errors);
        var screen = result.Screens.Single();
        Assert.Equal("desktop only for now", screen.ValueOf(WireframeModifierName.Note));
        Assert.Equal("minimaal 12 tekens", screen.Children[0].ValueOf(WireframeModifierName.Note));
        Assert.Equal("uit tot beide velden gevuld zijn", screen.Children[1].ValueOf(WireframeModifierName.Note));
        Assert.Equal(source, WireframeWriter.Write(result.Screens));
    }

    [Fact]
    public void AQuoteInsideANote_SurvivesBothDirections()
    {
        var source = """
            screen "X"
              button "Opslaan" note:"toon \"opgeslagen\" na klikken"
            """.ReplaceLineEndings("\n");

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

    // A refused viewport line is refused with the document intact: the screens below still render, and the document
    // keeps whatever viewport it legitimately had — none, or the one the first line already declared.
    public static IEnumerable<object[]> RefusedViewportLines() =>
    [
        ["viewport phablet\n\nscreen \"X\"", 1, new[] { "phablet", "desktop, tablet or mobile" }, null!],
        ["viewport mobile\nviewport tablet\n\nscreen \"X\"", 2, new[] { "already declares" }, WireframeViewport.Mobile],
        ["screen \"X\"\nviewport mobile", 2, new[] { "above the first screen" }, null!],
        ["  viewport mobile\n\nscreen \"X\"", 1, new[] { "left margin" }, null!],
    ];

    [Theory]
    [MemberData(nameof(RefusedViewportLines))]
    public void AViewportLineThatCannotStand_IsRefused_LeavingTheDocumentsOwnViewport(
        string source, int line, string[] present, object? viewport)
    {
        var result = WireframeParser.Parse(source);

        var error = Assert.Single(result.Errors);
        Assert.Equal(line, error.Line);
        Assert.All(present, fragment => Assert.Contains(fragment, error.Message, StringComparison.Ordinal));
        Assert.Equal((WireframeViewport?)viewport, result.Viewport);
        Assert.Single(result.Screens);
    }

    // ---- States (AC-914) ----

    [Fact]
    public void State_ReplacingAContainer_ParsesAndRoundTrips()
    {
        var source = """
            screen "Search results"
              main w:4
                list #results
                  item "Result 1"
              state "Empty" replaces:#results
                label "No results found"
            """.ReplaceLineEndings("\n");

        var result = WireframeParser.Parse(source);

        Assert.Empty(result.Errors);
        var screen = result.Screens.Single();
        var state = screen.Children.Single(child => child.Kind == WireframeNodeKind.State);
        Assert.Equal("Empty", state.Text);
        Assert.Equal("#results", state.ValueOf(WireframeModifierName.Replaces));
        Assert.Equal(source, WireframeWriter.Write(result.Screens));
    }

    [Fact]
    public void State_DeclaredBeforeItsContainer_StillResolves_BecauseAScreenIsReadWhole()
    {
        var result = WireframeParser.Parse("""
            screen "X"
              state "Empty" replaces:#results
                label "No results found"
              list #results
                item "Result 1"
            """);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void State_AtTheLeftMargin_IsRefused_BecauseAWireframeBeginsWithAScreen()
    {
        var result = WireframeParser.Parse("state \"Empty\" replaces:#c");

        Assert.Empty(result.Screens);
        Assert.NotEmpty(result.Errors);
    }
}
