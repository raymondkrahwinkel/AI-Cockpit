namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// The screens AC-871 measures the format against: a settings screen (nav + form + button row), a list-detail
// screen, an empty one, and one carrying the component ids of AC-906. Between them they use every container,
// widget and modifier.
internal static class WireframeScreens
{
    public const string Settings = """
        screen "Instellingen"
          row h:1
            column w:1
              nav
                item "Algemeen" selected
                item "Account"
                item "Meldingen"
                item "Sneltoetsen"
            column w:3
              group "Profiel"
                input "Profielnaam" value:"Raymond"
                input "E-mailadres" value:"raymond@krahwinkel.nl"
                input "Organisatie" disabled
              group "Thema"
                radio "Donker" checked
                radio "Licht"
              group "Meldingen"
                checkbox "Bureaubladmelding bij een afgeronde sessie" checked
                checkbox "Geluid bij een vraag van de agent"
                select "Waarschuwingsgeluid" value:"Zacht"
              space
              row align:right
                button "Annuleren"
                button "Opslaan" primary
        """;

    public const string ListDetail = """
        screen "Sessies"
          row h:1
            column w:2
              input "Zoeken" value:"AC-8"
              list "Actieve sessies" h:1
                item "AC-871 · wireframe-formaat" selected
                item "AC-870 · schil ontdubbelen"
                item "AC-849 · pins"
              button "Nieuwe sessie" primary
            column w:3
              group "AC-871 · wireframe-formaat"
                label "Een eigen tekstformaat voor wireframes, met parser en renderer."
                divider
                row
                  label "Stage" w:1
                  label "Develop" w:2
                row
                  label "Branch" w:1
                  label "cockpit/ac-871-wireframe" w:2
              tabs h:1
                tab "Transcript" selected
                  table "Laatste stappen"
                    item "Tijd"
                    item "Stap"
                    item "Uitkomst"
                tab "Bestanden"
                  list
                tab "Kosten"
                  image "Verbruik per dag"
              row align:right
                button "Verwijderen" disabled
                button "Openen" primary
        """;

    public const string Empty = """
        screen "Nieuw scherm"
        """;

    public const string Identified = """
        screen "Aanmelden" #login
          column w:1 #form
            input "E-mailadres" #email
            input "Wachtwoord" #password
            row align:right #actions
              button "Annuleren" #cancel
              button "Aanmelden" primary #submit
        """;

    public static TheoryData<string> Names => new() { nameof(Settings), nameof(ListDetail), nameof(Empty), nameof(Identified) };

    public static string Source(string name) => name switch
    {
        nameof(Settings) => Settings,
        nameof(ListDetail) => ListDetail,
        nameof(Identified) => Identified,
        _ => Empty,
    };
}
