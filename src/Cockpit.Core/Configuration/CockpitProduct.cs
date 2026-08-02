namespace Cockpit.Core.Configuration;

// What the app calls itself where a person reads it (AC-430).
// The name lives here once so a second naming round is an edit to one file instead of a sweep. The
// identifiers the product is *built* out of keep their own names deliberately — namespaces, the
// state folder in `CockpitBuild`, config keys, the repository — because renaming those breaks
// existing installs and their configuration and buys nothing a reader can see.
//
// Only the places that *name* the product use this: the title bar, the window title the taskbar
// reads, the About dialog, the tray, the header of a diagnostics report. Running prose says "the cockpit",
// the way the app already talks about itself elsewhere — a brand repeated in every sentence of the UI reads
// as advertising rather than as an explanation.
public static class CockpitProduct
{
    // The maker's half of the name — the word drawn at full strength in the title bar.
    public const string Brand = "Wispslate";

    // The product's own half — the faint word after the brand.
    public const string Product = "Cockpit";

    // Both halves, for the places that have a single string to put a name in.
    public const string DisplayName = $"{Brand} {Product}";
}
