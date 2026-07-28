namespace Cockpit.Core.Configuration;

/// <summary>
/// What the app calls itself where a person reads it (AC-430).
/// </summary>
/// <remarks>
/// The name lives here once so a second naming round is an edit to one file instead of a sweep. The
/// identifiers the product is <em>built</em> out of keep their own names deliberately — namespaces, the
/// state folder in <see cref="CockpitBuild"/>, config keys, the repository — because renaming those breaks
/// existing installs and their configuration and buys nothing a reader can see.
/// <para>
/// Only the places that <em>name</em> the product use this: the title bar, the window title the taskbar
/// reads, the About dialog, the tray, the header of a diagnostics report. Running prose says "the cockpit",
/// the way the app already talks about itself elsewhere — a brand repeated in every sentence of the UI reads
/// as advertising rather than as an explanation.
/// </para>
/// </remarks>
public static class CockpitProduct
{
    /// <summary>The maker's half of the name — the word drawn at full strength in the title bar.</summary>
    public const string Brand = "Wispslate";

    /// <summary>The product's own half — the faint word after the brand.</summary>
    public const string Product = "Cockpit";

    /// <summary>Both halves, for the places that have a single string to put a name in.</summary>
    public const string DisplayName = $"{Brand} {Product}";
}
