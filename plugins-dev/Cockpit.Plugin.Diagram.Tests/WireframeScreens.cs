namespace Cockpit.Plugin.Diagram.Tests;

// The screen every WireframeMcpTools test edits (AC-906 gives every component an id). AC-890: a hand-kept copy of
// tests/Cockpit.Infrastructure.Tests/Wireframe/WireframeScreens.cs — the original stays there for
// WireframeAccessRegistryTests and friends, which test code that did not move.
internal static class WireframeScreens
{
    // static readonly, not const: a raw string literal carries the checkout's line endings (core.autocrlf on
    // Windows), and the writer under test always emits \n — ReplaceLineEndings keeps the comparison checkout-agnostic.
    public static readonly string Settings = """
        screen "Instellingen" #screen
          row h:1 #row
            column w:1 #left
              nav #nav
                item "Algemeen" selected #general
                item "Account" #account
            column w:3 #right
              group "Profiel" #group
                input "Profielnaam" value:"Raymond" #name
                input "E-mailadres" #email
              row align:right #buttons
                button "Annuleren" #cancel
                button "Opslaan" primary #save
        """.ReplaceLineEndings("\n");

    public const string SaveButton = "save";
}
