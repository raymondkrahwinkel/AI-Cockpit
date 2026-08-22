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

    // AC-901: two screens in one document, with the blank line between them the writer puts there. What the
    // per-component tools have to keep apart — and every id is still unique across the whole document.
    public static readonly string TwoScreens = """
        screen "Aanmelden" #login
          input "E-mailadres" #login-email
          button "Aanmelden" primary #login-submit

        screen "Registreren" #signup
          input "E-mailadres" #signup-email
          button "Registreren" primary #signup-submit
        """.ReplaceLineEndings("\n");

    // AC-902: the same two screens, with a flow from the login screen's submit button to the signup screen.
    public static readonly string TwoScreensWithFlow = """
        screen "Aanmelden" #login
          input "E-mailadres" #login-email
          button "Aanmelden" primary goto:"Registreren" #login-submit

        screen "Registreren" #signup
          input "E-mailadres" #signup-email
          button "Registreren" primary #signup-submit
        """.ReplaceLineEndings("\n");

    public const string LoginScreen = "login";
    public const string LoginSubmit = "login-submit";
    public const string SignupScreen = "signup";
    public const string SignupSubmit = "signup-submit";

    public const string Screen = "screen";
    public const string Row = "row";
    public const string LeftColumn = "left";
    public const string Nav = "nav";
    public const string GeneralItem = "general";
    public const string AccountItem = "account";
    public const string Group = "group";
    public const string NameField = "name";
    public const string EmailField = "email";
    public const string ButtonRow = "buttons";
    public const string SaveButton = "save";

    public const int SaveButtonLine = 13;
}
