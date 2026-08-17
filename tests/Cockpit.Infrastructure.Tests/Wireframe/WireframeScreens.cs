namespace Cockpit.Infrastructure.Tests.Wireframe;

// The screen every wireframe-access test edits, with its line numbers written out — a component is addressed by its
// line, so a test that gets one wrong is testing the wrong component.
internal static class WireframeScreens
{
    public const string Settings = """
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

    public const int RowLine = 2;
    public const int NavLine = 4;
    public const int LeftColumnLine = 3;
    public const int GroupLine = 8;
    public const int NameFieldLine = 9;
    public const int EmailFieldLine = 10;
    public const int ButtonRowLine = 11;
    public const int SaveButtonLine = 13;

    public static string[] LinesOf(string source) => source.ReplaceLineEndings("\n").Split('\n');

    public static string LineOf(string source, int line) => LinesOf(source)[line - 1];
}
