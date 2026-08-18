namespace Cockpit.Infrastructure.Tests.Wireframe;

// The screen every wireframe-access test edits. Every component carries an id, because that is what a component is
// addressed by (AC-906) — a test naming the wrong one is testing the wrong component. `Plain` is the same screen as
// it was written before anything named it, for the tests about minting.
internal static class WireframeScreens
{
    public const string Settings = """
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
        """;

    public const string Plain = """
        screen "Instellingen"
          row h:1
            column w:1
              nav
                item "Algemeen" selected
                item "Account"
            column w:3
              group "Profiel"
                input "Profielnaam" value:"Raymond"
                input "E-mailadres"
              row align:right
                button "Annuleren"
                button "Opslaan" primary
        """;

    // AC-901: two screens in one document, with the blank line between them the writer puts there. What the
    // per-component tools have to keep apart — and every id is still unique across the whole document.
    public const string TwoScreens = """
        screen "Aanmelden" #login
          input "E-mailadres" #login-email
          button "Aanmelden" primary #login-submit

        screen "Registreren" #signup
          input "E-mailadres" #signup-email
          button "Registreren" primary #signup-submit
        """;

    public const string LoginScreen = "login";
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

    public static string[] LinesOf(string source) => source.ReplaceLineEndings("\n").Split('\n');

    public static string LineOf(string source, int line) => LinesOf(source)[line - 1];
}
