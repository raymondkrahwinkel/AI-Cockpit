namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// The screens the format is measured against: a settings screen (nav + form + button row), a list-detail
// screen, AC-903's product catalogue, an empty one, and one carrying the component ids of AC-906. Between them they
// use every container, widget and modifier.
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

    // AC-903's measure: the product overview from the ticket — header, breadcrumb, filter sidebar, a card grid,
    // pagination, a footer and a modal over it — written entirely in the vocabulary.
    public const string Catalogue = """
        screen "Catalogue"
          header "Northwind"
            search "Search products" w:3
            icon "Cart"
            badge value:"3" primary
            avatar "Raymond"
          breadcrumb
            item "Home"
            item "Catalogue"
            item "Bikes" selected
          row h:1
            sidebar "Filters" w:1
              checkbox "In stock" checked
              checkbox "On sale"
              toggle "Free shipping" checked
              slider "Price" value:60
              button "Apply"
            main w:4
              row
                label "128 results" w:1
                select "Sort by" value:"Newest"
                menu "Actions"
                  item "Export"
                  item "Compare"
              row h:1
                card "Trailhead 5" w:1
                  image
                  label "1.299"
                  badge value:"New"
                  progress "In stock" value:70
                  button "Add to cart" primary
                card "Ridgeline X" w:1
                  image
                  label "2.499"
                  badge value:"Sale" primary
                  progress "In stock" value:20
                  button "Add to cart" primary
                card w:1
                  image
                  label
                  label
                  button "Add to cart" primary
              pagination value:2 align:center
          footer "Northwind BV"
            label "Terms"
            label "Privacy"
          modal "Add to cart"
            stepper
              item "Options" selected
              item "Address"
              item "Payment"
            select "Size" value:"M"
            textarea "Note for the courier"
            row align:right
              button "Cancel"
              button "Continue" primary
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

    public static TheoryData<string> Names => new() { nameof(Settings), nameof(ListDetail), nameof(Catalogue), nameof(Empty), nameof(Identified) };

    public static string Source(string name) => name switch
    {
        nameof(Settings) => Settings,
        nameof(ListDetail) => ListDetail,
        nameof(Catalogue) => Catalogue,
        nameof(Identified) => Identified,
        _ => Empty,
    };
}
